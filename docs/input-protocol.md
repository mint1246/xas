# Input control protocol

This protocol carries physical keyboard and mouse events over the existing pinned mutual TLS connection. It requires the receiver's local `Input` grant and a real input-injection backend. The receiver advertises `Input` version 1 when that backend is available, or version 2 when it can also position the cursor absolutely. Only one peer may hold the receiver's input lease at a time.

- RPC `input.open`: empty payload. Response is a nonzero four-byte big-endian session ID.
- Client `StreamData`, method `input.event`, `RequestId = 0`, `StreamId = sessionId`: one to 1024 fixed-width events, at most 12,288 bytes. Each event is 12 bytes: kind (1), USB HID keyboard usage or mouse button code (2 big-endian), flags (1: bit 0 down, bit 1 repeat), X (4 signed big-endian), Y (4 signed big-endian). Key codes use HID keyboard page 0x07 usages 4–231; mouse buttons use codes 1–8. Move X/Y are relative pixels; scroll X/Y use 1/120 wheel-detent units, with positive X scrolling right and positive Y scrolling up. A zero `KeepAlive` event keeps an idle lease alive.
- RPC `input.close`: four-byte big-endian session ID. The receiver releases all pressed keys and buttons, then releases the lease.
- Client `StreamEnd` for `input.event` with empty payload also closes the lease.
- Version 2 adds `MoveAbsolute` (kind 5), with nonnegative X/Y pixel coordinates, no code or flags. Automatic Windows monitor handoff requires version 2 and the `Display` capability. The controller requests `display.info` after authenticating; this RPC requires the receiver's Input grant and returns the active Linux display dimensions. It maps the Windows virtual monitor rectangle to those dimensions, then sends `MoveAbsolute` on each native cursor move.

The receiver tracks every down/up key and button. On close, disconnect, invalid event, injected-backend failure, or missing keepalive for four seconds, it releases every held key/button and the lease. The Windows sender stops capture on transport failure, cursor exit, or the emergency local-control chord. Input events are not accepted before `input.open` completes. SudoVDA and the portal helper still require physical validation.
