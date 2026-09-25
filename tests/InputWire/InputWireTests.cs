using Xas.Core;

namespace Xas.Tests;

public static class InputWireTests
{
    public static Task RunAsync()
    {
        var events = new[]
        {
            new InputEvent(InputEventKind.Move, X: -4, Y: 15),
            new InputEvent(InputEventKind.Button, Code: 5, Down: true),
            new InputEvent(InputEventKind.Scroll, X: 30, Y: -120),
            new InputEvent(InputEventKind.Key, Code: 4, Down: true, Repeat: true),
            new InputEvent(InputEventKind.Key, Code: 4, Down: false),
            new InputEvent(InputEventKind.KeepAlive)
        };
        var decoded = InputWire.Decode(InputWire.Encode(events));
        if (!events.SequenceEqual(decoded)) throw new Exception("Input event batch did not roundtrip.");
        AssertThrows<InvalidDataException>(() => InputWire.Decode([]));
        AssertThrows<InvalidDataException>(() => InputWire.Decode(new byte[11]));
        AssertThrows<InvalidDataException>(() => InputWire.Decode(new byte[12 * (InputWire.MaxEventsPerFrame + 1)]));
        AssertThrows<ArgumentException>(() => InputWire.Encode([new InputEvent(InputEventKind.Key, Code: 0, Down: true)]));
        var invalidFlags = InputWire.Encode([new InputEvent(InputEventKind.KeepAlive)]);
        invalidFlags[3] = 0x80;
        AssertThrows<InvalidDataException>(() => InputWire.Decode(invalidFlags));
        return Task.CompletedTask;
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
