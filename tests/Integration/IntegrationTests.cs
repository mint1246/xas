using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xas.Cli;
using Xas.Cli.Interactive;
using Xas.Cli.FileTransfer;
using Xas.Cli.Clipboard;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Core.Services;
using Xas.Core.Transport;
using Xas.Daemon;
using Xas.Daemon.Sessions;
using Xas.Daemon.Shell;

namespace Xas.Tests;

public static class IntegrationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "xas-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousIpcInstance = Environment.GetEnvironmentVariable("XAS_LOCAL_IPC_INSTANCE");
        Environment.SetEnvironmentVariable("XAS_LOCAL_IPC_INSTANCE", "integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var serverIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "server"));
            using var clientIdentity = DeviceIdentity.LoadOrCreate(Path.Combine(root, "client"));
            var serverTrust = new PeerTrustStore(Path.Combine(root, "server-trust"));
            var clientTrust = new PeerTrustStore(Path.Combine(root, "client-trust"));
            serverTrust.Approve(clientIdentity.DeviceId, clientIdentity.Fingerprint, "client");
            clientTrust.Approve(serverIdentity.DeviceId, serverIdentity.Fingerprint, "server");
            var permissions = new PeerPermissionStore(Path.Combine(root, "server-trust"));
            permissions.SetAllowed(clientIdentity.DeviceId, Capability.Shell, true);
            permissions.SetAllowed(clientIdentity.DeviceId, Capability.FileSystem, true);
            permissions.SetAllowed(clientIdentity.DeviceId, Capability.Clipboard, true);
            var inputBackend = new RecordingInputBackend();
            var remoteClipboard = new RecordingTextClipboard("remote initial");
            var daemonConfiguration = new LocalConfiguration(Path.Combine(root, "daemon-config"));

            var port = ReservePort();
            using var stop = new CancellationTokenSource();
            var daemon = new DaemonHost(serverIdentity, serverTrust, permissions, port,
                inputBackend, remoteClipboard, configuration: daemonConfiguration, webPort: 0,
                enableNativeOrchestration: false);
            var serverTask = daemon.RunAsync(stop.Token);
            try
            {
                await Task.Delay(300);
                await using var tls = await MutualTlsTransport.ConnectAsync("127.0.0.1", port, clientIdentity,
                    clientTrust, serverIdentity.DeviceId, TimeSpan.FromSeconds(10));
                await using var frames = new BinaryFrameConnection(tls.Stream, leaveOpen: true);
                await using var peer = new MultiplexedProtocolPeer(frames, (_, _) =>
                    ValueTask.FromException<ProtocolMessage>(new NotSupportedException()));

                var info = await peer.RequestAsync("device.info", []);
                var device = JsonSerializer.Deserialize<DeviceInfo>(info.Payload);
                Assert(device?.DeviceId == serverIdentity.DeviceId, "Info returned the wrong device.");
                if (device is null) throw new Exception("Info response was empty.");
                Assert(device.Capabilities.Any(c => c.Capability == Capability.Shell && c.Version >= 2), "Streaming shell capability was not advertised.");
                Assert(device.Capabilities.Any(c => c.Capability == Capability.Input && c.Version == 1),
                    "Available input backend was not advertised.");

                daemonConfiguration.UpsertPeer(new ConfiguredPeer(clientIdentity.DeviceId, "client", "127.0.0.1", 9));
                using (var localDaemon = new LocalDaemonClient(Stream.Null, Stream.Null, Stream.Null))
                {
                    var localDevices = await localDaemon.ListDevicesAsync(CancellationToken.None);
                    Assert(localDevices.Any(d => d.DeviceId == clientIdentity.DeviceId),
                        "The local daemon IPC device list did not expose configured daemon state.");
                    await localDaemon.SetDefaultDeviceAsync(clientIdentity.DeviceId, CancellationToken.None);
                    Assert(daemonConfiguration.DefaultDeviceId == clientIdentity.DeviceId,
                        "The local daemon IPC default-device update did not mutate daemon configuration.");
                    var uiUrl = await localDaemon.GetUiUrlAsync(CancellationToken.None);
                    Assert(Uri.TryCreate(uiUrl, UriKind.Absolute, out var uiUri) && uiUri.IsLoopback && uiUri.Port > 0,
                        "The local daemon IPC did not return a valid loopback UI URL.");
                    if (uiUri is null) throw new InvalidOperationException("The local daemon IPC returned an invalid UI URL.");
                    if (OperatingSystem.IsWindows())
                    {
                        var mounts = await localDaemon.ListRemoteMountsAsync(null, CancellationToken.None);
                        Assert(mounts.Count == 0,
                            "A fresh integration daemon unexpectedly reported a native remote mount.");
                    }
                    await ExerciseWebUiAsync(uiUri, daemonConfiguration, permissions, clientIdentity.DeviceId);
                }

                try
                {
                    await peer.RequestAsync("input.open", []);
                    throw new Exception("Input access succeeded without a grant.");
                }
                catch (RemoteProtocolException) { }
                permissions.SetAllowed(clientIdentity.DeviceId, Capability.Input, true);
                var inputOpen = await peer.RequestAsync("input.open", []);
                var inputId = BinaryPrimitives.ReadUInt32BigEndian(inputOpen.Payload);
                await peer.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, inputId, "input.event",
                    InputWire.Encode([new InputEvent(InputEventKind.Key, 4, Down: true),
                        new InputEvent(InputEventKind.Move, X: 11, Y: -5)])));
                var inputCloseBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(inputCloseBytes, inputId);
                await peer.RequestAsync("input.close", inputCloseBytes);
                Assert(inputBackend.Events.Count == 2 && inputBackend.ReleaseCount == 1,
                    "Input events or cleanup failed through the network protocol.");

                var shellRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-args", "hello world"]);
                var reply = await peer.RequestAsync("shell.run", ShellWire.EncodeRequest(shellRequest));
                var result = ShellWire.DecodeResult(reply.Payload);
                Assert(result.ExitCode == 0 && Encoding.UTF8.GetString(result.StandardOutput) == "hello world",
                    "The remote argv shell path failed.");

                var config = new LocalConfiguration(Path.Combine(root, "client-config"));
                config.UpsertPeer(new ConfiguredPeer(serverIdentity.DeviceId, "server", "127.0.0.1", port));
                config.SetDefault(serverIdentity.DeviceId);
                var localClipboard = new RecordingTextClipboard("local initial");
                using (var syncStop = new CancellationTokenSource())
                {
                    var syncTask = ClipboardClient.SyncAsync(null, config, clientIdentity, clientTrust,
                        localClipboard, TextWriter.Null, syncStop.Token);
                    try
                    {
                        await WaitUntilAsync(() => localClipboard.ReadCount >= 2 && remoteClipboard.ReadCount >= 2);
                        await localClipboard.SetTextAsync("from local", CancellationToken.None);
                        await WaitUntilAsync(() => remoteClipboard.Text == "from local");
                        await remoteClipboard.SetTextAsync("from remote", CancellationToken.None);
                        await WaitUntilAsync(() => localClipboard.Text == "from remote");
                        var remoteWrites = remoteClipboard.WriteCount;
                        await Task.Delay(1600);
                        Assert(remoteClipboard.WriteCount == remoteWrites,
                            "Clipboard sync echoed an imported value back to its origin.");
                    }
                    finally
                    {
                        syncStop.Cancel();
                        try { await syncTask; } catch (OperationCanceledException) { }
                    }
                }
                using var output = new MemoryStream();
                using var error = new MemoryStream();
                using var cliClient = new RemoteXasClient(config, clientIdentity, clientTrust, output, error);
                var largeRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-output", "1500000"]);
                var largeExit = await cliClient.RunShellAsync(largeRequest, null, CancellationToken.None);
                Assert(largeExit == 0 && output.Length == 1_500_000 && error.Length == 0,
                    "The CLI client did not receive the full binary output stream.");

                output.SetLength(0);
                using var pipedInput = new MemoryStream(Encoding.UTF8.GetBytes("piped input\n"));
                using var pipedClient = new RemoteXasClient(config, clientIdentity, clientTrust, output, error, pipedInput);
                var inputRequest = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    [typeof(IntegrationTests).Assembly.Location, "--echo-stdin"]);
                var inputExit = await pipedClient.RunShellAsync(inputRequest, null, CancellationToken.None);
                Assert(inputExit == 0 && Encoding.UTF8.GetString(output.ToArray()) == "piped input\n",
                    "The CLI client did not forward binary standard input.");

                using var ipcOutputA = new MemoryStream();
                using var ipcOutputB = new MemoryStream();
                using var ipcClientA = new LocalDaemonClient(Stream.Null, ipcOutputA, Stream.Null);
                using var ipcClientB = new LocalDaemonClient(Stream.Null, ipcOutputB, Stream.Null);
                await using var ipcInteractiveTls = await MutualTlsTransport.ConnectAsync("127.0.0.1", port,
                    clientIdentity, clientTrust, serverIdentity.DeviceId, TimeSpan.FromSeconds(10));
                await using var ipcInteractiveFrames = new BinaryFrameConnection(ipcInteractiveTls.Stream, leaveOpen: true);
                var clientPermissions = new PeerPermissionStore(Path.Combine(root, "client-trust"));
                clientPermissions.SetAllowed(serverIdentity.DeviceId, Capability.Shell, true);
                var ipcShellBackend = new GatedIpcShellBackend();
                await using var remoteShell = new StreamingCommandManager(serverIdentity.DeviceId,
                    clientPermissions, ipcInteractiveFrames.SendAsync, ipcShellBackend);
                await using var ipcInteractivePeer = new MultiplexedProtocolPeer(ipcInteractiveFrames,
                    (request, token) => request.Method is ShellExecWire.Open or ShellExecWire.Close
                        ? remoteShell.HandleRequestAsync(request, token)
                        : ValueTask.FromException<ProtocolMessage>(new NotSupportedException()));
                ipcInteractivePeer.MessageReceived += message => remoteShell.HandleMessageAsync(message);
                _ = await ipcInteractivePeer.RequestAsync("session.lane.open",
                    Encoding.UTF8.GetBytes(PeerLane.Interactive.ToString()), cancellationToken: CancellationToken.None);
                var ipcRequestA = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    ["ipc-alpha"]);
                var ipcRequestB = new ShellRequest(ShellMode.Exec, null, "dotnet",
                    ["ipc-beta"]);
                var ipcRun = Task.WhenAll(
                    ipcClientA.RunShellAsync(ipcRequestA, clientIdentity.DeviceId, CancellationToken.None),
                    ipcClientB.RunShellAsync(ipcRequestB, clientIdentity.DeviceId, CancellationToken.None));
                await ipcShellBackend.BothCommandsStarted.WaitAsync(TimeSpan.FromSeconds(5));
                await ipcInteractivePeer.SendAsync(new ProtocolMessage(MessageKind.Event, 0, 0,
                    "peer.notice", Encoding.UTF8.GetBytes("unrelated control event")));
                ipcShellBackend.ReleaseCommands();
                var ipcResults = await ipcRun.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(ipcResults.SequenceEqual([0, 0]) &&
                    Encoding.UTF8.GetString(ipcOutputA.ToArray()) == "ipc-alpha" &&
                    Encoding.UTF8.GetString(ipcOutputB.ToArray()) == "ipc-beta",
                    $"Concurrent bound IPC shell commands received unexpected data (exit {string.Join(',', ipcResults)}, " +
                    $"A='{Encoding.UTF8.GetString(ipcOutputA.ToArray())}', B='{Encoding.UTF8.GetString(ipcOutputB.ToArray())}').");

                using var blockedInput = new SynchronousBlockingInputStream();
                using var blockedOutput = new MemoryStream();
                using var blockedClient = new LocalDaemonClient(blockedInput, blockedOutput, Stream.Null);
                Task<int>? blockedRun = null;
                try
                {
                    blockedRun = Task.Run(() => blockedClient.RunShellAsync(
                        new ShellRequest(ShellMode.Exec, null, "fake", ["ipc-blocked"]),
                        clientIdentity.DeviceId, CancellationToken.None));
                    await blockedInput.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    await ipcShellBackend.BlockedCommandStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    ipcShellBackend.ReleaseBlockedCommand();
                    var blockedExit = await blockedRun.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(blockedExit == 0 && Encoding.UTF8.GetString(blockedOutput.ToArray()) == "ipc-blocked" &&
                        !blockedInput.IsReleased,
                        "Local IPC shell completion waited for a synchronous stdin read to return.");
                }
                finally
                {
                    ipcShellBackend.ReleaseBlockedCommand();
                    blockedInput.Release();
                    if (blockedRun is not null)
                        try { await blockedRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    await blockedInput.ReadFinished.WaitAsync(TimeSpan.FromSeconds(5));
                }

                if (OperatingSystem.IsWindows())
                {
                    using var interactiveInput = new MemoryStream(Encoding.UTF8.GetBytes("echo XAS_INTERACTIVE_OK\r\nexit /b 7\r\n"));
                    using var interactiveOutput = new MemoryStream();
                    using var interactiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var interactiveExit = await InteractiveShellClient.RunAsync(
                        config.Resolve(null)!, clientIdentity, clientTrust, interactiveInput, interactiveOutput,
                        80, 24, interactiveTimeout.Token);
                    var interactiveText = Encoding.UTF8.GetString(interactiveOutput.ToArray());
                    Assert(interactiveExit == 7 && interactiveText.Contains("XAS_INTERACTIVE_OK", StringComparison.Ordinal),
                        $"Networked ConPTY shell failed: exit {interactiveExit}, output '{interactiveText}'.");
                }

                var uploadSource = Path.Combine(root, "source.bin");
                var uploadBytes = new byte[1_500_000];
                Random.Shared.NextBytes(uploadBytes);
                await File.WriteAllBytesAsync(uploadSource, uploadBytes);
                var remoteDir = Path.Combine(root, "remote-files");
                Directory.CreateDirectory(remoteDir);
                await FileCopyClient.CopyAsync(uploadSource, "server:" + remoteDir + Path.DirectorySeparatorChar,
                    recursive: false, overwrite: false, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                var remoteFile = Path.Combine(remoteDir, "source.bin");
                Assert(File.ReadAllBytes(remoteFile).SequenceEqual(uploadBytes), "Uploaded file content did not match.");
                var downloadDir = Path.Combine(root, "downloads");
                Directory.CreateDirectory(downloadDir);
                await FileCopyClient.CopyAsync("server:" + remoteFile, downloadDir,
                    recursive: false, overwrite: false, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                Assert(File.ReadAllBytes(Path.Combine(downloadDir, "source.bin")).SequenceEqual(uploadBytes),
                    "Downloaded file content did not match.");
                var projectDir = Path.Combine(root, "project");
                Directory.CreateDirectory(Path.Combine(projectDir, "nested"));
                await File.WriteAllTextAsync(Path.Combine(projectDir, "nested", "héllo.txt"), "recursive copy ✓");
                await FileCopyClient.CopyAsync(projectDir, "server:" + remoteDir + Path.DirectorySeparatorChar,
                    recursive: true, overwrite: false, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                await FileCopyClient.CopyAsync("server:" + Path.Combine(remoteDir, "project"), downloadDir,
                    recursive: true, overwrite: false, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                Assert(await File.ReadAllTextAsync(Path.Combine(downloadDir, "project", "nested", "héllo.txt")) == "recursive copy ✓",
                    "Recursive Unicode file copy failed.");
                try
                {
                    await FileCopyClient.CopyAsync(uploadSource, "server:" + remoteFile,
                        recursive: false, overwrite: false, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                    throw new Exception("File copy overwrote an existing destination without --force.");
                }
                catch (RemoteProtocolException) { }
                await File.WriteAllTextAsync(uploadSource, "overwritten deliberately");
                await FileCopyClient.CopyAsync(uploadSource, "server:" + remoteFile,
                    recursive: false, overwrite: true, config, clientIdentity, clientTrust, TextWriter.Null, CancellationToken.None);
                Assert(await File.ReadAllTextAsync(remoteFile) == "overwritten deliberately",
                    "Forced overwrite did not replace the remote file.");

                permissions.SetAllowed(clientIdentity.DeviceId, Capability.FileSystem, false);
                try
                {
                    await peer.RequestAsync("file.stat", JsonSerializer.SerializeToUtf8Bytes(new { path = remoteFile }));
                    throw new Exception("Denied file system request succeeded.");
                }
                catch (RemoteProtocolException) { }

                permissions.SetAllowed(clientIdentity.DeviceId, Capability.Shell, false);
                try
                {
                    await peer.RequestAsync("shell.open", JsonSerializer.SerializeToUtf8Bytes(
                        new { columns = 80, rows = 24, elevated = false }));
                    throw new Exception("Denied interactive shell request succeeded.");
                }
                catch (RemoteProtocolException) { }
                try
                {
                    await peer.RequestAsync("shell.run", ShellWire.EncodeRequest(shellRequest));
                    throw new Exception("Denied shell request succeeded.");
                }
                catch (RemoteProtocolException) { }
            }
            finally
            {
                stop.Cancel();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAS_LOCAL_IPC_INSTANCE", previousIpcInstance);
            Directory.Delete(root, recursive: true);
        }
    }

    private static int ReservePort()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var first = new TcpListener(IPAddress.Any, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            first.Stop();
            if (port >= 65535) continue;
            try
            {
                using var control = new TcpListener(IPAddress.Any, port);
                using var pairing = new TcpListener(IPAddress.Any, port + 1);
                control.Start(); pairing.Start();
                control.Stop(); pairing.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("Could not reserve consecutive loopback ports for the daemon integration test.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class GatedIpcShellBackend : IShellBackend
    {
        private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public bool SupportsInteractive => false;
        public Task BothCommandsStarted => _bothStarted.Task;
        public Task BlockedCommandStarted => _blockedStarted.Task;
        public void ReleaseCommands() => _release.TrySetResult();
        public void ReleaseBlockedCommand() => _blockedRelease.TrySetResult();

        public async Task<int> RunAsync(ShellRequest request, Stream stdin, Stream stdout, Stream stderr,
            CancellationToken cancellationToken)
        {
            var blocked = request.Arguments?.LastOrDefault() == "ipc-blocked";
            if (blocked) _blockedStarted.TrySetResult();
            else if (Interlocked.Increment(ref _started) == 2) _bothStarted.TrySetResult();
            await (blocked ? _blockedRelease.Task : _release.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            var output = Encoding.UTF8.GetBytes(request.Arguments?.LastOrDefault() ?? string.Empty);
            await stdout.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class SynchronousBlockingInputStream : Stream
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public Task ReadFinished => _readFinished.Task;
        public bool IsReleased => _release.IsSet;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            _release.Wait(); // Models terminal FileStream reads that block before returning a ValueTask.
            _readFinished.TrySetResult();
            return ValueTask.FromResult(0);
        }
        public void Release() => _release.Set();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Release(); _release.Dispose(); }
            base.Dispose(disposing);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ExerciseWebUiAsync(Uri uiUri, LocalConfiguration configuration,
        PeerPermissionStore permissions, string peerId)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var client = new HttpClient(handler) { BaseAddress = uiUri, Timeout = TimeSpan.FromSeconds(5) };

        var page = await client.GetStringAsync("/");
        Assert(page.Contains("id=\"tab-storage\"", StringComparison.Ordinal),
            "The local web UI did not render the Storage tab.");
        Assert(page.Contains("id=\"volume-list\"", StringComparison.Ordinal),
            "The local web UI did not render remote-volume controls.");
        var marker = "const csrf='";
        var csrfStart = page.IndexOf(marker, StringComparison.Ordinal);
        Assert(csrfStart >= 0, "The local web UI did not embed its CSRF token.");
        csrfStart += marker.Length;
        var csrfEnd = page.IndexOf('\'', csrfStart);
        Assert(csrfEnd > csrfStart, "The local web UI embedded an invalid CSRF token.");
        var csrf = page[csrfStart..csrfEnd];

        using (var stateRequest = new HttpRequestMessage(HttpMethod.Get, "/api/state"))
        {
            stateRequest.Headers.Add("X-Xas-CSRF", csrf);
            using var stateResponse = await client.SendAsync(stateRequest);
            Assert(stateResponse.IsSuccessStatusCode, "The local web UI state endpoint rejected a valid session.");
            using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
            Assert(state.RootElement.TryGetProperty("storage", out var storage),
                "The local web UI state did not expose storage state.");
            Assert(storage.TryGetProperty("AutoExposeRemovable", out _),
                "The local web UI storage state omitted the local removable-drive setting.");
            Assert(storage.TryGetProperty("Mounts", out _),
                "The local web UI storage state omitted current native mounts.");
        }

        async Task<HttpResponseMessage> PostAsync(string path, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Xas-CSRF", csrf);
            request.Headers.Add("Origin", uiUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
            return await client.SendAsync(request);
        }

        using (var settingsResponse = await PostAsync("/api/storage/settings",
                   "{\"autoExposeRemovable\":false,\"autoMountRemoteRemovable\":true}"))
        {
            Assert(settingsResponse.IsSuccessStatusCode, "The local web UI storage settings endpoint rejected valid settings.");
            Assert(!configuration.AutoExposeRemovable && configuration.AutoMountRemoteRemovable,
                "Camel-case web UI storage settings were not applied to daemon configuration.");
        }

        using (var permissionResponse = await PostAsync("/api/permission",
                   $"{{\"deviceId\":\"{peerId}\",\"capability\":\"Clipboard\",\"allowed\":false}}"))
        {
            Assert(permissionResponse.IsSuccessStatusCode,
                "The local web UI permission endpoint rejected a valid camel-case request.");
            Assert(!permissions.IsAllowed(peerId, Capability.Clipboard),
                "Camel-case web UI permission changes were not applied to the peer permission store.");
        }
        permissions.SetAllowed(peerId, Capability.Clipboard, true);

        using (var badMount = await PostAsync("/api/storage/mount",
                   $"{{\"deviceId\":\"{peerId}\",\"volume\":\"missing-volume\"}}"))
        {
            Assert(badMount.StatusCode == HttpStatusCode.BadRequest,
                "An invalid web UI storage mount did not return HTTP 400.");
            using var error = JsonDocument.Parse(await badMount.Content.ReadAsStringAsync());
            Assert(error.RootElement.TryGetProperty("error", out var message) && !string.IsNullOrWhiteSpace(message.GetString()),
                "An invalid web UI storage mount did not return a readable JSON error.");
        }

        using var restore = await PostAsync("/api/storage/settings",
            "{\"autoExposeRemovable\":true,\"autoMountRemoteRemovable\":true}");
        Assert(restore.IsSuccessStatusCode, "The local web UI could not restore storage settings after the test.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    private sealed class RecordingTextClipboard(string initialText) : ITextClipboardBackend
    {
        private readonly object _gate = new();
        private string _text = initialText;
        private ulong _changeId = 1;
        private int _reads, _writes;
        public bool IsAvailable => true;
        public string Text { get { lock (_gate) return _text; } }
        public int ReadCount { get { lock (_gate) return _reads; } }
        public int WriteCount { get { lock (_gate) return _writes; } }
        public ValueTask<ClipboardTextSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        { lock (_gate) { _reads++; return ValueTask.FromResult(new ClipboardTextSnapshot(_text, _changeId)); } }
        public ValueTask SetTextAsync(string text, CancellationToken cancellationToken)
        { lock (_gate) { _text = text; _changeId++; _writes++; return ValueTask.CompletedTask; } }
    }

    private sealed class RecordingInputBackend : IInputInjectionBackend
    {
        public bool IsAvailable => true;
        public List<InputEvent> Events { get; } = [];
        public int ReleaseCount { get; private set; }
        public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken)
        { Events.Add(inputEvent); return ValueTask.CompletedTask; }
        public ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
        { ReleaseCount++; return ValueTask.CompletedTask; }
    }
}
