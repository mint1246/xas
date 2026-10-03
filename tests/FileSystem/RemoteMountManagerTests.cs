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
            var ejectCount = 0;
            var failEject = false;
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
                },
                ejectVolume: (_, volumeId, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Equal("sd-1", volumeId, "Eject targeted the wrong remote volume.");
                    ejectCount++;
                    if (failEject) return ValueTask.FromException(new IOException("remote media is busy"));
                    current = [];
                    return ValueTask.CompletedTask;
                },
                mountPointFactory: (_, _, volume) => $"mount/{volume.Id}",
                availabilityProbe: () => true);
            var run = manager.RunAsync(CancellationToken.None);

            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Assert(manager.NativeMountsAvailable, "Injected native mount availability probe was ignored.");
            Equal(1, adapters.Count, "Initial removable volume mounted more than once.");
            Equal("mount/sd-1", adapters[0].RequestedMountPoint,
                "Remote mount manager did not pass the platform mount point to the native adapter.");
            Equal("Z:", manager.GetSnapshots()[0].MountPoint, "Mounted drive letter was not surfaced.");
            Equal("CAMERA_SD", manager.GetSnapshots()[0].VolumeName, "Original remote volume name was not retained in state.");

            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(2));
            await Task.Delay(100);
            Equal(1, adapters.Count, "A duplicate session event created a duplicate mount.");

            Assert(await manager.UnmountVolumeAsync(peerId, "CAMERA_SD", CancellationToken.None),
                "Manual unmount did not report an existing mount.");
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[0].UnmountCount, "Manual unmount did not close the native mount.");
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(3));
            await Task.Delay(150);
            Equal(0, manager.GetSnapshots().Count, "Manual unmount was immediately undone by automatic remounting.");
            Equal(1, adapters.Count, "Suppressed volume unexpectedly created another mount adapter.");

            var manuallyMounted = await manager.MountVolumeAsync(peerId, "CAMERA_SD", CancellationToken.None);
            Equal("Y:", manuallyMounted.MountPoint, "Manual mount did not create the expected native mount.");
            Equal(2, adapters.Count, "Manual mount did not create exactly one new mount adapter.");

            await manager.EjectVolumeAsync(peerId, "CAMERA_SD", CancellationToken.None);
            Equal(1, ejectCount, "Remote eject callback was not invoked.");
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[1].UnmountCount, "Successful remote eject did not first remove the local native mount.");

            // Let the post-eject reconciliation observe that the remote volume is genuinely gone;
            // this clears suppression so a later physical reinsertion of the same volume ID can auto-mount.
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(3.5));
            await Task.Delay(150);

            current = [new RemoteVolume("sd-1", "CAMERA_SD", "removable", false, 64_000_000, 31_000_000, "exfat")];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(4));
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1 && adapters.Count == 3);

            failEject = true;
            try
            {
                await manager.EjectVolumeAsync(peerId, "CAMERA_SD", CancellationToken.None);
                throw new InvalidOperationException("Failed remote eject unexpectedly succeeded.");
            }
            catch (IOException ex) when (ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)) { }
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1 && adapters.Count == 4);
            Equal(1, adapters[2].UnmountCount, "Failed eject did not remove the original local mount first.");
            Equal("W:", manager.GetSnapshots()[0].MountPoint,
                "Failed remote eject did not clear suppression and restore the still-present remote volume.");
            failEject = false;

            configuration.SetAutoMountRemoteRemovable(false);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[3].UnmountCount, "Disabling automatic remote mounts left the drive mounted.");

            configuration.SetAutoMountRemoteRemovable(true);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1 && adapters.Count == 5);

            current =
            [
                new RemoteVolume("sd-1", "CAMERA_SD", "removable", false, 64_000_000, 31_000_000, "exfat"),
                new RemoteVolume("export-projects", "PROJECTS", "export", false, 128_000_000, 96_000_000, "ext4")
            ];
            var exportMount = await manager.MountVolumeAsync(peerId, "PROJECTS", CancellationToken.None);
            Equal("export", exportMount.Kind, "Manual export mount lost its remote volume kind.");
            Equal(6, adapters.Count, "Manual export mount did not create exactly one native mount.");

            configuration.SetAutoMountRemoteRemovable(false);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Equal("export-projects", manager.GetSnapshots()[0].VolumeId,
                "Disabling automatic removable mounts removed an explicitly requested export mount.");
            Equal(1, adapters[4].UnmountCount,
                "Disabling automatic removable mounts did not remove the automatic removable mount.");

            configuration.SetAutoMountRemoteRemovable(true);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 2 && adapters.Count == 7);
            Assert(manager.GetSnapshots().Any(m => m.VolumeId == "export-projects"),
                "Re-enabling automatic mounts removed the explicit export mount.");

            session.Detach(PeerLane.Bulk, bulk);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[5].UnmountCount, "Disconnect did not close the explicit export mount.");
            Assert(await session.AttachAsync(PeerLane.Bulk, bulk), "Could not restore the test bulk lane.");
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 2 && adapters.Count == 9);
            Assert(manager.GetSnapshots().Any(m => m.VolumeId == "export-projects"),
                "Reconnect did not restore the explicit export mount.");

            current = [current[0]];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(5));
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Equal(1, adapters[7].UnmountCount, "Removing the remote export did not clear its manual mount.");
            current =
            [
                current[0],
                new RemoteVolume("export-projects", "PROJECTS", "export", false, 128_000_000, 96_000_000, "ext4")
            ];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(6));
            await Task.Delay(150);
            Equal(1, manager.GetSnapshots().Count,
                "A disappeared export retained stale manual intent and was mounted when it reappeared.");

            await manager.MountVolumeAsync(peerId, "PROJECTS", CancellationToken.None);
            Assert(await manager.UnmountVolumeAsync(peerId, "PROJECTS", CancellationToken.None),
                "Manual export unmount did not report an existing native mount.");

            var main = new RemoteVolume("main-root", "root", "fixed", false, 128_000_000, 96_000_000, "ext4");
            current = [current[0], main];
            session.UpdateMetadata(roundTripTime: TimeSpan.FromMilliseconds(7));
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 2);
            Assert(manager.GetSnapshots().Any(m => m.VolumeId == main.Id),
                "The main drive was not mounted automatically.");
            configuration.SetAutoMountRemoteRemovable(false);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Equal(main.Id, manager.GetSnapshots()[0].VolumeId,
                "The removable-drive setting removed the automatic main-drive mount.");
            configuration.SetAutoMountRemoteMainDrive(false);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            configuration.SetAutoMountRemoteMainDrive(true);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Assert(await manager.UnmountVolumeAsync(peerId, main.Id, CancellationToken.None),
                "Main-drive unmount failed.");
            configuration.SetAutoMountRemoteRemovable(true);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 1);
            Equal("sd-1", manager.GetSnapshots()[0].VolumeId,
                "A manually unmounted main drive was automatically remounted.");
            await manager.MountVolumeAsync(peerId, main.Id, CancellationToken.None);
            configuration.SetAutoMountRemoteMainDrive(false);
            await Task.Delay(150);
            Assert(manager.GetSnapshots().Any(m => m.VolumeId == main.Id),
                "Disabling automatic main-drive mounting removed an explicit mount.");

            session.Detach(PeerLane.Bulk, bulk);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Assert(await session.AttachAsync(PeerLane.Bulk, bulk), "Could not restore the bulk lane for main-drive reconnect.");
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 2);
            Assert(manager.GetSnapshots().Any(m => m.VolumeId == main.Id),
                "Reconnect did not restore the explicit main-drive mount.");

            session.Detach(PeerLane.Bulk, bulk);
            await WaitUntilAsync(() => manager.GetSnapshots().Count == 0);
            Equal(1, adapters[8].UnmountCount, "Losing the bulk lane left a stale drive mounted.");

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
        public string? RequestedMountPoint { get; private set; }
        public int MountCount { get; private set; }
        public int UnmountCount { get; private set; }

        public ValueTask MountAsync(string requestedMountPoint, RemoteVolume volume,
            IRemoteFileSystemOperations remoteFileSystem, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MountCount++;
            RequestedMountPoint = requestedMountPoint;
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
