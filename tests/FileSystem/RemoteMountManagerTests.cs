using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.FileSystem;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Daemon;
using Xas.Daemon.FileSystem.Mount;
using Xas.Daemon.Input;
using Xas.Daemon.Sessions;

namespace Xas.Tests;

public static class RemoteMountManagerTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-remote-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "identity"), "desktop");
            var trust = new PeerTrustStore(Path.Combine(root, "trust"));
            var permissions = new PeerPermissionStore(Path.Combine(root, "trust"));
            var configuration = new LocalConfiguration(Path.Combine(root, "config"));
            const string peerId = "xas-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            configuration.UpsertPeer(new ConfiguredPeer(peerId, "laptop", "127.0.0.1", 47821));

            await using var input = new InputControlService(permissions, new UnavailableInputBackend());
            var dispatcher = new RequestDispatcher(identity, permissions, configuration: configuration);
            await using var sessions = new PeerSessionManager(identity, trust, permissions, input, dispatcher, configuration);
            sessions.UpdateConfiguredPeers(configuration.Peers);
            var session = sessions.GetSession(peerId) ?? throw new InvalidOperationException("Configured peer session was not created.");

            await using var control = CreateIdlePeer();
            await using var bulk = CreateIdlePeer();
            Assert(await session.AttachAsync(PeerLane.Control, control), "Could not attach test control lane.");
            Assert(await session.AttachAsync(PeerLane.Bulk, bulk), "Could not attach test bulk lane.");
            session.UpdateMetadata(device: new DeviceInfo(peerId, "laptop", "Linux", "x64",
                [new CapabilityVersion(Capability.FileSystem, 1)]));

            RemoteVolume[] current =
            [
                new RemoteVolume("sd-1", "CAMERA_SD", "removable", false, 64_000_000, 32_000_000, "exfat")
            ];
            var adapters = new List<FakeMountAdapter>();
            await using var manager = new RemoteMountManager(configuration, sessions,
                adapterFactory: () =>
                {
                    var adapter = new FakeMountAdapter($"{(char)('Z' - adapters.Count)}:");
                    adapters.Add(adapter);
                    return adapter;
                },
                volumeProvider: (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(current.ToArray());
                });
            var run = manager.RunAsync(CancellationToken.None);

            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Equal(1, adapters.Count, "Initial removable volume mounted more than once.");
            Equal("Z:", manager.GetSnapshots()[0].MountPoint, "Mounted drive letter was not surfaced.");
            Equal("CAMERA_SD", manager.GetSnapshots()[0].VolumeName, "Original remote volume name was not retained in state.");

            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(2));
            await Task.Delay(100);
            Equal(1, adapters.Count, "A duplicate session event created a duplicate mount.");

            current = [];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(3));
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[0].UnmountCount, "Remote volume removal did not unmount its drive.");

            current = [new RemoteVolume("sd-1", "CAMERA_SD", "removable", false, 64_000_000, 31_000_000, "exfat")];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(4));
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1 && adapters.Count == 2);

            configuration.SetAutoMountRemoteRemovable(false);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[1].UnmountCount, "Disabling automatic remote mounts left the drive mounted.");

            configuration.SetAutoMountRemoteRemovable(true);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1 && adapters.Count == 3);

            session.Detach(PeerLane.Bulk, bulk);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[2].UnmountCount, "Losing the bulk lane left a stale drive mounted.");

            await manager.DisposeAsync();
            await run;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MultiplexedProtocolPeer CreateIdlePeer()
    {
        var frames = new BinaryFrameConnection(new MemoryStream(), leaveOpen: false);
        return new MultiplexedProtocolPeer(frames, (_, _) =>
            ValueTask.FromException<ProtocolMessage>(new NotSupportedException()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeMountAdapter(string mountPoint) : IRemoteFileSystemMountAdapter
    {
        public bool IsAvailable => true;
        public string PlatformName => "FakeMount";
        public string? MountedAt { get; private set; }
        public int MountCount { get; private set; }
        public int UnmountCount { get; private set; }

        public ValueTask MountAsync(string requestedMountPoint, RemoteVolume volume,
            IRemoteFileSystemOperations remoteFileSystem, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MountCount++;
            MountedAt = mountPoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask UnmountAsync(CancellationToken cancellationToken)
        {
            UnmountCount++;
            MountedAt = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
