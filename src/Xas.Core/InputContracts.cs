using System.Buffers.Binary;

namespace Xas.Core;

/// <summary>Portable physical input events. Key codes use USB HID keyboard page 0x07 usages.</summary>
public enum InputEventKind : byte { KeepAlive = 0, Move = 1, Button = 2, Scroll = 3, Key = 4, MoveAbsolute = 5 }

/// <summary>Move uses relative pixels; scroll uses units of 1/120 wheel detent.</summary>
public readonly record struct InputEvent(InputEventKind Kind, ushort Code = 0,
    bool Down = false, bool Repeat = false, int X = 0, int Y = 0);

/// <summary>Stage markers for opt-in input-pipeline timing. Timestamps are Stopwatch ticks local to each process.</summary>
public enum InputPipelineStage : byte { Capture = 0, Enqueue = 1, Send = 2, Receive = 3, Inject = 4 }

/// <summary>A batch-level pipeline sample; consumers can derive durations between stages in the same process.</summary>
public readonly record struct InputPipelineMetric(InputPipelineStage Stage, long Timestamp, int EventCount);

/// <summary>Optional observer. Keep Record nonblocking; a null observer avoids per-event timestamp work.</summary>
public interface IInputPipelineMetrics
{
    void Record(in InputPipelineMetric metric);
}

/// <summary>Allows an input backend to preserve a received batch through its local injection path.</summary>
public interface IInputBatchInjectionBackend
{
    ValueTask InjectBatchAsync(IReadOnlyList<InputEvent> events, CancellationToken cancellationToken);
}

/// <summary>Fixed-width 12-byte encoding for batches of input events.</summary>
public static class InputWire
{
    public const int EventBytes = 12;
    public const int MaxEventsPerFrame = 1024;

    public static byte[] Encode(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count is < 1 or > MaxEventsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(events));
        var bytes = new byte[checked(events.Count * EventBytes)];
        for (var i = 0; i < events.Count; i++)
        {
            var value = events[i];
            Validate(value);
            var span = bytes.AsSpan(i * EventBytes, EventBytes);
            span[0] = (byte)value.Kind;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..3], value.Code);
            span[3] = (byte)((value.Down ? 1 : 0) | (value.Repeat ? 2 : 0));
            BinaryPrimitives.WriteInt32BigEndian(span[4..8], value.X);
            BinaryPrimitives.WriteInt32BigEndian(span[8..12], value.Y);
        }
        return bytes;
    }

    public static InputEvent[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % EventBytes != 0 || bytes.Length / EventBytes > MaxEventsPerFrame)
            throw new InvalidDataException("Invalid input event batch size.");
        var events = new InputEvent[bytes.Length / EventBytes];
        for (var i = 0; i < events.Length; i++)
        {
            var span = bytes.Slice(i * EventBytes, EventBytes);
            if ((span[3] & ~3) != 0) throw new InvalidDataException("Unknown input event flags.");
            var value = new InputEvent((InputEventKind)span[0],
                BinaryPrimitives.ReadUInt16BigEndian(span[1..3]),
                (span[3] & 1) != 0, (span[3] & 2) != 0,
                BinaryPrimitives.ReadInt32BigEndian(span[4..8]),
                BinaryPrimitives.ReadInt32BigEndian(span[8..12]));
            try { Validate(value); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid input event.", ex); }
            events[i] = value;
        }
        return events;
    }

    private static void Validate(InputEvent value)
    {
        var noCodeOrFlags = value.Code == 0 && !value.Down && !value.Repeat;
        var noPosition = value.X == 0 && value.Y == 0;
        var valid = value.Kind switch
        {
            InputEventKind.KeepAlive => noCodeOrFlags && noPosition,
            InputEventKind.Move or InputEventKind.Scroll => noCodeOrFlags,
            InputEventKind.MoveAbsolute => noCodeOrFlags && value.X >= 0 && value.Y >= 0,
            InputEventKind.Button => value.Code is >= 1 and <= 8 && noPosition && !value.Repeat,
            InputEventKind.Key => value.Code is >= 4 and <= 231 && noPosition && (!value.Repeat || value.Down),
            _ => false
        };
        if (!valid) throw new ArgumentException("Input event has an invalid kind, code, or field combination.", nameof(value));
    }
}
