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
- [x] Linux display metadata for GNOME on Wayland, read over Mutter's DisplayConfig D-Bus API, including logical-layout scale and transform handling.
- [x] Wayland portal/libei input helper source.
- [x] Replace the in-tree unsigned driver with the SudoVDA control channel: automatic monitor create/remove at the Linux display's mode, stable per-display monitor identity, driver watchdog keepalive, and adapter-based monitor discovery that needs no configuration.

## In progress

- [x] Compile the Linux PTY helper on Ubuntu 24.04 and pass a local framed-protocol smoke test.
- [ ] Validate the full interactive shell path between Windows and Linux.
- [ ] Validate file copy and clipboard commands between a real Windows and Linux pair.
- [ ] Validate clipboard sync on a physical Windows/Linux pair, including simultaneous edits and reconnect after sleep.
- [x] Exercise the SudoVDA control channel against an installed driver: create, reconfigure, and remove monitors, confirm the watchdog keepalive holds a monitor past its timeout, and confirm the monitor identity rules.
- [x] Attach the virtual display at startup regardless of other virtual monitors, pin input to the monitor the daemon created, and recover when the driver will not republish a previously used identity.
- [x] Read the remote display mode on GNOME Wayland through Mutter's DisplayConfig D-Bus API, including logical-layout scale.
- [ ] Validate monitor-boundary handoff on a real Windows/Linux pair with the remote mode applied.
- [x] Build the Wayland portal/libei helper on Ubuntu 24.04.
- [ ] Validate compositor consent, absolute input, revocation, and release behavior on a real Wayland desktop.
- [ ] Validate physical Windows cursor/keyboard handoff with X11 and Wayland receivers, including disconnect and emergency return.
- [x] Configure virtual display modes dynamically from Linux metadata.
- [ ] Reconnect long-lived sessions after sleep/network changes.
- [ ] Complete capability negotiation for all services and per-peer grants.

## Pending

- [ ] Verify fully networked Linux PTY interactive shells on Linux.
- [ ] Linux sudo and Windows elevated-user service broker.
- [ ] Clipboard images and richer formats.
- [ ] WinFsp and FUSE mounts, removable media, and transfer resume.
- [ ] Local web configuration UI, installation, and startup services.
