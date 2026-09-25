# Interactive shell protocol, version 1

This contract is scoped to one mutually authenticated, multiplexed TLS connection. Session IDs are nonzero 32-bit numbers allocated by the receiver and are never reused within that connection. The receiver's local `Shell` grant is checked when opening a session. Elevation requests remain unsupported until a real broker exists.

- RPC `shell.open`: JSON `{ "columns": 80, "rows": 24, "elevated": false }`. Response payload is the session ID as four big-endian bytes. The receiver starts a real PTY/ConPTY and returns an error if unavailable.
- Client `StreamData`, method `shell.input`, `StreamId = sessionId`, `RequestId = 0`: raw terminal input bytes, at most 64 KiB per frame.
- Client `StreamEnd`, method `shell.input`, same ID: stop accepting further input for this session. The receiver keeps its PTY input handle alive until the shell exits or the session closes, because closing a ConPTY input pipe can interrupt commands already queued.
- Server `StreamData`, method `shell.output`, same ID: raw UTF-8/VT terminal output, at most 64 KiB per frame.
- RPC `shell.resize`: payload is session ID (4-byte big-endian), columns (2-byte big-endian), rows (2-byte big-endian); response is empty.
- RPC `shell.close`: payload is session ID (4-byte big-endian); response is empty. It closes and reaps the session.
- Server `Event`, method `shell.exit`, `StreamId = sessionId`, payload is signed 32-bit exit code in big-endian format. Output frames precede this event.

Unknown IDs and invalid dimensions must return errors. Disconnect closes every session and releases resources. The CLI restores local terminal settings in a `finally` block. The receiver never grants privileges through this protocol.
