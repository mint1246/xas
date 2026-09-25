# Input control protocol, version 1

This protocol carries physical keyboard and mouse events over the existing pinned mutual TLS connection. It requires the receiver's local `Input` grant and a real input-injection backend. The receiver advertises `Input` version 1 only when that backend is available. Only one peer may hold the receiver's input lease at a time.

- RPC `input.open`: empty payload. Response is a nonzero four-byte big-endian session ID.
- Client `StreamData`, method `input.event`, `RequestId = 0`, `StreamId = sessionId`: one to 1024 fixed-width events, at most 12,288 bytes. Each event is 12 bytes: kind (1), USB HID keyboard usage or mouse button code (2 big-endian), flags (1: bit 0 down, bit 1 repeat), X (4 signed big-endian), Y (4 signed big-endian). Key codes use HID keyboard page 0x07 usages 4–231; mouse buttons use codes 1–8. Move X/Y are relative pixels; scroll X/Y use 1/120 wheel-detent units. A zero `KeepAlive` event keeps an idle lease alive.
- RPC `input.close`: four-byte big-endian session ID. The receiver releases all pressed keys and buttons, then releases the lease.
- Client `StreamEnd` for `input.event` with empty payload also closes the lease.

The receiver tracks every down/up key and button. On close, disconnect, invalid event, injected-backend failure, or missing keepalive for four seconds, it releases every held key/button and the lease. The Windows sender must stop capturing locally on transport failure or an emergency local-control chord independent of the receiver. Input events are not accepted before `input.open` completes. A future native Windows virtual monitor will provide monitor-boundary handoff; the initial manual capture mode does not claim that monitor integration.
