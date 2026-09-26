using Xas.Core;
using Xas.Core.Protocol;

namespace Xas.Input;

/// <summary>Stable authenticated peer transport used by native hot input routing.</summary>
public interface IHotInputPeer
{
    DisplayMetadata? Display { get; }
    bool RealtimeReady { get; }
    event Action? Changed;
    event Action? Disconnected;
    ValueTask<ProtocolMessage> RequestRealtimeAsync(string method, byte[] payload,
        CancellationToken cancellationToken);
    ValueTask SendRealtimeAsync(ProtocolMessage message, CancellationToken cancellationToken);
}
