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
                inputBackend, remoteClipboard, configuration: daemonConfiguration, webPort: 0);
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
                    if (OperatingSystem.IsWindows())
                    {
                        var mounts = await localDaemon.ListRemoteMountsAsync(null, CancellationToken.None);
                        Assert(mounts.Count == 0,
                            "A fresh integration daemon unexpectedly reported a native remote mount.");
                    }
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
            using var first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            first.Stop();
            if (port >= 65535) continue;
            try
            {
                using var control = new TcpListener(IPAddress.Loopback, port);
                using var pairing = new TcpListener(IPAddress.Loopback, port + 1);
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
