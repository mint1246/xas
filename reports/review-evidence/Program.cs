// Isolated review reproductions. No daemon/service startup, display IOCTLs, mounts, or input hooks.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.FileSystem;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Daemon;
using Xas.Daemon.Input;
using Xas.Daemon.Sessions;
using Xas.Daemon.FileSystem.Mount;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This review harness targets Windows.");
var assembly = typeof(DaemonHost).Assembly;
var ipcType = assembly.GetType("Xas.Daemon.LocalIpc.LocalIpcServer")!;
var forwardingType = ipcType.GetNestedType("ForwardedShell", BindingFlags.NonPublic)!;
var session = new PeerSession("review-peer", "Review peer");
var received = new List<ProtocolMessage>();
Func<ProtocolMessage, CancellationToken, ValueTask> send = (message, _) =>
{ received.Add(message); return ValueTask.CompletedTask; };
var forwarding = Activator.CreateInstance(forwardingType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    null, [session, 1u, send], null)!;
await (Task)forwardingType.GetMethod("SetRemoteId")!.Invoke(forwarding, [101u])!;
await (ValueTask)forwardingType.GetMethod("OnRemoteMessageAsync")!.Invoke(forwarding,
    [session, PeerLane.Interactive, new ProtocolMessage(MessageKind.Event, 0, 102, ShellExecWire.Exit, ShellExecWire.EncodeExitCode(37))])!;
Check(received.Count == 1 && received[0].StreamId == 1, "Shell forwarder accepts another command's exit and rewrites it to its local ID");
await ((IAsyncDisposable)forwarding).DisposeAsync();

var volume = new RemoteVolume("review", "Review", "export", false, null, null, null);
var remoteFailure = new RemoteProtocolException("fs.stat", Encoding.UTF8.GetBytes("Could not find file 'missing.txt'."));
var remote = new RemoteFileSystemOperationsClient(volume.Id, (_, _, _) => ValueTask.FromException<ProtocolMessage>(remoteFailure));
var winfs = new RemoteWinFspFileSystem(volume, remote);
var map = typeof(RemoteWinFspFileSystem).GetMethod("MapException", BindingFlags.NonPublic | BindingFlags.Instance)!;
var localStatus = (int)map.Invoke(winfs, [new FileNotFoundException("missing")])!;
Exception actual;
try { await remote.StatAsync("missing.txt", CancellationToken.None); throw new Exception("Expected failure"); }
catch (RemoteProtocolException ex) { actual = ex; }
var remoteStatus = (int)map.Invoke(winfs, [actual])!;
Check(localStatus != remoteStatus, $"Missing-file wire error loses native status: local=0x{localStatus:X8}, remote=0x{remoteStatus:X8}");
var fuseType = assembly.GetType("Xas.Daemon.FileSystem.Mount.RemoteFuseOperations")!;
var fuseMap = fuseType.GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;
try
{
    var fuseLocal = fuseMap.Invoke(null, [new FileNotFoundException("missing")])!;
    var fuseRemote = fuseMap.Invoke(null, [actual])!;
    Check(!fuseLocal.Equals(fuseRemote), $"FUSE missing-file mapping also differs: local={fuseLocal}, remote={fuseRemote}");
}
catch (TargetInvocationException ex) { Console.WriteLine($"NOT RUN: FUSE native type initialization: {ex.InnerException?.GetType().Name}"); }

var root = Path.Combine(AppContext.BaseDirectory, "isolated-config");
var configuration = new LocalConfiguration(root);
var permissions = new PeerPermissionStore(Path.Combine(root, "trust"));
var trust = new PeerTrustStore(Path.Combine(root, "trust"));
using var key = RSA.Create(2048);
using var cert = new CertificateRequest("CN=Review", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
using var identity = (DeviceIdentity)typeof(DeviceIdentity).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
    .Single().Invoke(["review-local", "review-fingerprint", cert]);
await using var input = new InputControlService(permissions, new UnavailableInputBackend());
var dispatcher = new RequestDispatcher(identity, permissions, configuration: configuration);
var manager = new PeerSessionManager(identity, trust, permissions, input, dispatcher, configuration);
var stream = new RespondingStream();
var runConnection = typeof(PeerSessionManager).GetMethod("RunConnectionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
var laneTask = (Task)runConnection.Invoke(manager,
    ["review-peer", "Review peer", stream, true, (PeerLane?)PeerLane.Realtime, CancellationToken.None])!;
await stream.Handshake.Task.WaitAsync(TimeSpan.FromSeconds(2));
var dialers = (ConcurrentDictionary<string, Task>)typeof(PeerSessionManager)
    .GetField("_dialers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
dialers["review-peer|Realtime"] = laneTask;
var shutdown = manager.DisposeAsync().AsTask();
await Task.Delay(250);
Check(!shutdown.IsCompleted && !laneTask.IsCompleted, "PeerSessionManager disposal remains blocked on its live outbound lane after shutdown cancellation");
// Close only the synthetic connection, allowing clean disposal without abandoning a task.
stream.CloseInbound();
await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

// A manual mount of an explicitly exported directory is removed by automatic reconciliation.
var mountSessions = new PeerSessionManager(identity, trust, permissions, input, dispatcher, configuration);
configuration.UpsertPeer(new ConfiguredPeer("review-export-peer", "Review export peer", "127.0.0.1", 47821));
mountSessions.UpdateConfiguredPeers(configuration.Peers);
var mountPeer = mountSessions.GetSession("review-export-peer")!;
await using var controlLane = new MultiplexedProtocolPeer(new IdleFrames(), (_, _) => throw new NotSupportedException());
await using var bulkLane = new MultiplexedProtocolPeer(new IdleFrames(), (_, _) => throw new NotSupportedException());
var attach = typeof(PeerSession).GetMethod("AttachAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
await (ValueTask<bool>)attach.Invoke(mountPeer, [PeerLane.Control, controlLane, true])!;
await (ValueTask<bool>)attach.Invoke(mountPeer, [PeerLane.Bulk, bulkLane, true])!;
typeof(PeerSession).GetMethod("UpdateMetadata", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(mountPeer,
    [new DeviceInfo(mountPeer.DeviceId, "Review", "Linux", "x64", [new(Capability.FileSystem, 2)]), null, null, false]);
var exportVolume = volume with { Id = "export-documents", Name = "Documents" };
var adapter = new FakeAdapter();
await using var mounts = new RemoteMountManager(configuration, mountSessions,
    adapterFactory: () => adapter, volumeProvider: (_, _) => ValueTask.FromResult(new[] { exportVolume }));
await mounts.MountVolumeAsync(mountPeer.DeviceId, exportVolume.Id, CancellationToken.None);
Check(mounts.GetSnapshots().Count == 1, "Manual export mount initially succeeds with a fake native adapter");
await (Task)typeof(RemoteMountManager).GetMethod("ReconcileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(mounts, [mountPeer.DeviceId, CancellationToken.None])!;
Check(mounts.GetSnapshots().Count == 0 && adapter.Unmounted, "Automatic reconciliation removes a manually mounted export that is still available");
await mountSessions.DisposeAsync();

Console.WriteLine("Review reproductions completed; no live daemon, service, monitor, or mount was started.");

static void Check(bool observed, string description)
{
    if (!observed) throw new Exception("Not reproduced: " + description);
    Console.WriteLine("CONFIRMED: " + description);
}

// In-memory peer that replies to the lane handshake, then remains idle until explicitly closed.
sealed class RespondingStream : Stream
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _position;
    public TaskCompletionSource Handshake { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void CloseInbound() => _inbound.Writer.TryComplete();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _position == _current.Length)
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_inbound.Reader.TryRead(out _current)) continue;
            _position = 0;
        }
        var count = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytes = buffer.ToArray();
        var methodLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(15, 2));
        var method = Encoding.UTF8.GetString(bytes, 17, methodLength);
        if (bytes[6] == (byte)MessageKind.Request)
        {
            var response = bytes[..(17 + methodLength)];
            BinaryPrimitives.WriteUInt32BigEndian(response, (uint)(response.Length - 4));
            response[6] = (byte)MessageKind.Response;
            _inbound.Writer.TryWrite(response);
            if (method == "session.lane.open") Handshake.TrySetResult();
        }
        return ValueTask.CompletedTask;
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class IdleFrames : IFrameConnection
{
    public ValueTask SendAsync(ProtocolMessage message, CancellationToken token) => ValueTask.CompletedTask;
    public async ValueTask<ProtocolMessage?> ReceiveAsync(CancellationToken token)
    { await Task.Delay(Timeout.Infinite, token); return null; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeAdapter : IRemoteFileSystemMountAdapter
{
    public bool IsAvailable => true;
    public string PlatformName => "Review fake";
    public string? MountedAt { get; private set; }
    public bool Unmounted { get; private set; }
    public ValueTask MountAsync(string mountPoint, RemoteVolume volume, IRemoteFileSystemOperations remote, CancellationToken token)
    { MountedAt = "review-only"; return ValueTask.CompletedTask; }
    public ValueTask UnmountAsync(CancellationToken token)
    { Unmounted = true; MountedAt = null; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
