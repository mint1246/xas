using System.Buffers.Binary;
using Xas.Core;
using Xas.Core.Security;
using Xas.Daemon.Input;

namespace Xas.Tests;

public static class InputControlTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "xas-input-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            await CheckLeaseLifecycleAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task CheckLeaseLifecycleAsync(string directory)
    {
        var permissions = new PeerPermissionStore(directory);
        var backend = new RecordingBackend();
        await using var service = new InputControlService(permissions, backend);
        await using var peer = service.CreateSession("peer");
        await ExpectFailureAsync<UnauthorizedAccessException>(
            () => peer.HandleRequestAsync(Request("input.open"), default).AsTask());

        permissions.SetAllowed("peer", Capability.Input, true);
        var opened = await peer.HandleRequestAsync(Request("input.open"), default);
        var leaseId = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
        Assert(leaseId != 0, "The lease ID must be nonzero.");

        await using var other = service.CreateSession("other");
        permissions.SetAllowed("other", Capability.Input, true);
        await ExpectLeaseBusyAsync(other);

        var keyEvent = new InputEvent(InputEventKind.Key, 4, Down: true);
        var streamMessage = new ProtocolMessage(
            MessageKind.StreamData, 0, leaseId, "input.event", InputWire.Encode([keyEvent]));
        await peer.HandleMessageAsync(streamMessage);

        var encodedLeaseId = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(encodedLeaseId, leaseId);
        var releaseRequest = new ProtocolMessage(
            MessageKind.Request, 2, 0, "input.release", encodedLeaseId);
        await peer.HandleRequestAsync(releaseRequest, default);
        Assert(backend.ReleaseCalls == 1,
            "Returning to the local monitor must release held remote input.");

        await ExpectLeaseBusyAsync(other);
        backend.BlockRelease = true;
        var disconnect = peer.DisposeAsync().AsTask();
        await backend.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(backend.ReleaseCalls == 2,
            "Closing a held lease must release backend input.");
        await ExpectLeaseBusyAsync(other);

        backend.FinishRelease();
        await disconnect;
        var reopened = await other.HandleRequestAsync(Request("input.open"), default);
        var reopenedLeaseId = BinaryPrimitives.ReadUInt32BigEndian(reopened.Payload);
        Assert(reopenedLeaseId != 0,
            "A subsequent peer must acquire the released lease.");

        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert(backend.ReleaseCalls == 3,
            "The inactivity watchdog did not release the second lease.");

        await CheckUnsupportedInputAsync(service, permissions, backend);
    }

    private static async Task CheckUnsupportedInputAsync(
        InputControlService service,
        PeerPermissionStore permissions,
        RecordingBackend backend)
    {
        await using var third = service.CreateSession("third");
        permissions.SetAllowed("third", Capability.Input, true);
        var opened = await third.HandleRequestAsync(Request("input.open"), default);
        var leaseId = BinaryPrimitives.ReadUInt32BigEndian(opened.Payload);
        var moveEvent = new InputEvent(InputEventKind.MoveAbsolute, X: 1, Y: 2);
        var message = new ProtocolMessage(
            MessageKind.StreamData, 0, leaseId, "input.event", InputWire.Encode([moveEvent]));

        await ExpectFailureAsync<NotSupportedException>(
            () => third.HandleMessageAsync(message).AsTask());
        Assert(backend.ReleaseCalls == 4,
            "Unsupported absolute input did not release the lease.");
    }

    private static async Task ExpectLeaseBusyAsync(ConnectionInputSession peer)
    {
        await ExpectFailureAsync<InvalidOperationException>(
            () => peer.HandleRequestAsync(Request("input.open"), default).AsTask());
    }

    private static ProtocolMessage Request(string method) =>
        new(MessageKind.Request, 1, 0, method, []);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ExpectFailureAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class RecordingBackend : IInputInjectionBackend
    {
        private readonly TaskCompletionSource _releaseGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;
        public int ReleaseCalls { get; private set; }
        public bool BlockRelease { get; set; }
        public TaskCompletionSource ReleaseStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InjectAsync(InputEvent inputEvent, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            if (!BlockRelease)
            {
                return;
            }

            ReleaseStarted.TrySetResult();
            await _releaseGate.Task.WaitAsync(cancellationToken);
        }

        public void FinishRelease() => _releaseGate.TrySetResult();
    }
}
