using System.Buffers.Binary;
using Xas.Core;

namespace Xas.Daemon.Input;

/// <summary>
/// Local helper protocol: big-endian uint16 event count followed by InputWire's 12-byte events.
/// Zero events requests release of held keys/buttons; the EIS helper returns an ASCII "OK\n" acknowledgement.
/// </summary>
internal static class LinuxInputHelperWire
{
    public static byte[] Encode(IReadOnlyList<InputEvent> events)
    {
        var payload = InputWire.Encode(events);
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)events.Count));
        payload.CopyTo(frame, 2);
        return frame;
    }

    public static readonly byte[] Release = [0, 0];
}
