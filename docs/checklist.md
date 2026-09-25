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
- [x] Explicit authenticated `xas cp` for streamed files and recursive directories, overwrite protection, timestamps, and separate FileSystem grants.
- [x] Explicit plain-text clipboard push/pull, per-peer Clipboard grants, origin/version tracking, Windows native clipboard roundtrip, and Linux tool-based backend source.
- [x] Manual Windows input capture, authenticated input protocol, Input grants, exclusive lease, disconnect/timeout release, Windows SendInput receiver, and opt-in X11 XTest receiver source.
- [x] Continuous plain-text clipboard sync with reconnect, echo suppression, deterministic simultaneous-edit resolution, and a network loopback test.
- [x] Managed automatic Windows monitor-boundary handoff, absolute pointer protocol, Linux display metadata RPC, and topology/coordinate tests.
- [x] Source for one-monitor Windows IddCx driver and Wayland portal/libei input helper.
- [x] Build the Windows x64 IddCx DLL with the Microsoft-signed WDK NuGet toolchain; verify its INF with Windows Driver, Universal, and WHQL rules.

## In progress

- [x] Compile the Linux PTY helper on Ubuntu 24.04 and pass a local framed-protocol smoke test.
- [ ] Validate the full interactive shell path between Windows and Linux.
- [ ] Validate file copy and clipboard commands between a real Windows and Linux pair.
- [ ] Validate clipboard sync on a physical Windows/Linux pair, including simultaneous edits and reconnect after sleep.
- [ ] Generate/sign an installable catalog, install the IddCx package, extend it as a second Windows monitor, and validate native monitor identification.
- [x] Build the Wayland portal/libei helper on Ubuntu 24.04.
- [ ] Validate compositor consent, absolute input, revocation, and release behavior on a real Wayland desktop.
- [ ] Validate physical Windows cursor/keyboard handoff with X11 and Wayland receivers, including disconnect and emergency return.
- [ ] Configure virtual display modes dynamically from Linux metadata; currently modes are fixed.
- [ ] Reconnect long-lived sessions after sleep/network changes.
- [ ] Complete capability negotiation for all services and per-peer grants.

## Pending

- [ ] Verify fully networked Linux PTY interactive shells on Linux.
- [ ] Linux sudo and Windows elevated-user service broker.
- [ ] Clipboard images and richer formats.
- [ ] WinFsp and FUSE mounts, removable media, and transfer resume.
- [ ] Local web configuration UI, installation, and startup services.
