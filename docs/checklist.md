# Implementation checklist

## Complete

- [x] Inspect repository, Git state, and installed SDK.
- [x] Establish shared contracts and solution structure.
- [x] Verify key platform choices against current upstream documentation.
- [x] Persistent device identity, manual two-sided pairing, pinned mutual TLS, and local shell grants.
- [x] Bounded binary framing, request IDs, concurrent requests, cancellation, and binary stream messages.
- [x] LAN discovery announcements and peer tracking.
- [x] One-shot shell `-c` and argv `exec`, exit status, streamed stdout/stderr, and bounded piped stdin.
- [x] Windows ConPTY backend with local terminal/resize/exit test.
- [x] Linux PTY helper source and managed bridge compile on Windows; Linux runtime test pending.
- [x] Authenticated endpoint refresh from LAN discovery after a stale address fails.
- [x] Combined loopback tests and first working Git commit.
- [x] Authenticated interactive shell protocol, per-connection session cleanup, Windows networked ConPTY test, and local Windows/Linux raw terminal adapters.

## In progress

- [ ] Compile and run the Linux PTY helper and full interactive path on Linux.
- [ ] Reconnect long-lived sessions after sleep/network changes.
- [ ] Complete capability negotiation for all services and per-peer grants.

## Pending

- [ ] Verify fully networked Linux PTY interactive shells on Linux.
- [ ] Linux sudo and Windows elevated-user service broker.
- [ ] Windows input capture, Linux portal/libei injection, and disconnect safety.
- [ ] Windows IddCx virtual display.
- [ ] Clipboard synchronization.
- [ ] Filesystem RPC, WinFsp and FUSE mounts, removable media, transfer.
- [ ] Local web configuration UI, installation, and startup services.
