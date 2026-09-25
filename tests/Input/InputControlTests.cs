using System.Buffers.Binary;
using Xas.Core;
using Xas.Core.Security;
using Xas.Daemon.Input;

namespace Xas.Tests;

/// <summary>Focused contract tests; intentionally kept in tests/Input so the owning test harness can wire them.</summary>
public static class InputControlTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xas-input-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var permissions = new PeerPermissionStore(directory);
            var backend = new RecordingBackend();
            await using var service = new InputControlService(permissions, backend);
            await using var peer = service.CreateSession("peer");
            await Throws<UnauthorizedAccessException>(() => peer.HandleRequestAsync(Request("input.open"), default).AsTask());
            permissions.SetAllowed("peer", Capability.Input, true);
            var opened = await peer.HandleRequestAsync(Request("input.open"), default);
            var id = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
            Assert(id != 0, "The lease ID must be nonzero.");
            await using var other = service.CreateSession("other");
            permissions.SetAllowed("other", Capability.Input, true);
            await Throws<InvalidOperationException>(() => other.HandleRequestAsync(Request("input.open"), default).AsTask());
            await peer.HandleMessageAsync(new ProtocolMessage(MessageKind.StreamData, 0, id, "input.event",
                InputWire.Encode([new InputEvent(InputEventKind.Key, 4, Down: true)])));
            backend.BlockRelease = true;
            var disconnect = peer.DisposeAsync().AsTask();
            await backend.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(backend.ReleaseCalls == 1, "Closing a held lease must release backend input.");
            await Throws<InvalidOperationException>(() => other.HandleRequestAsync(Request("input.open"), default).AsTask());
            backend.FinishRelease();
            await disconnect;
            var reopened = await other.HandleRequestAsync(Request("input.open"), default);
            Assert(BinaryPrimitives.ReadUInt32BigEndian(reopened.Payload) != 0, "A subsequent peer must acquire the released lease.");
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert(backend.ReleaseCalls == 2, "The inactivity watchdog did not release the second lease.");
            await using var third = service.CreateSession("third");
            permissions.SetAllowed("third", Capability.Input, true);
            await third.HandleRequestAsync(Request("input.open"), default);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ProtocolMessage Request(string method) => new(MessageKind.Request, 1, 0, method, []);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class RecordingBackend : IInputInjectionBackend
    {
        public bool IsAvailable => true;
        public int ReleaseCalls { get; private set; }
        public bool BlockRelease { get; set; }
        public TaskCompletionSource ReleaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            if (BlockRelease) { ReleaseStarted.TrySetResult(); await _releaseGate.Task.WaitAsync(cancellationToken); }
        }
        public void FinishRelease() => _releaseGate.TrySetResult();
    }
}
