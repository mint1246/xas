# xas implementation contracts

The user-session daemon owns networking and authorization. Each installation has a persistent certificate identity and a local peer trust store. A connection must mutually authenticate before any service request is accepted. Advertised capabilities describe implementation availability; local peer grants separately determine access.

Projects: `Xas.Core` owns shared types, protocol, identity, and configuration; `Xas.Daemon` hosts services; `Xas.Cli` is the `xas` command. The initial transport is TLS over TCP if current QUIC deployment requirements make it less portable. Protocol version 1 uses bounded binary frames and explicit request IDs. Payloads may be UTF-8 JSON for control records; bulk bytes must stay binary. Unknown capabilities and message versions fail explicitly.

Initial shell modes: `Command` runs through the target OS shell; `Exec` uses executable plus argv; `Interactive` requires a real PTY/ConPTY. No line-at-a-time interactive fallback. Elevation is a separate authorization and remains unavailable until a real platform broker exists.

Current service methods: `device.info` returns available capability versions; `device.ping` checks the authenticated connection; `shell.run` is bounded v1 output; `shell.stream` is v2 with a binary stdin field and separate binary stdout/stderr frames. The receiver checks its local `PeerPermissionStore` before running a shell. The CLI queries `device.info` to select v2 or v1. Connection establishment always pins the expected remote device key.

The next shell step is an interactive session lifecycle over multiplexed streams with terminal resize, EOF, and disconnect cleanup. Windows ConPTY implements the platform side. The Linux platform side uses a [small native helper](../native/linux-pty/README.md) because `forkpty` inside the managed, multithreaded daemon is unsafe; its Linux runtime behavior is unverified. Address discovery updates a stale configured endpoint only after the discovered address passes pinned mutual TLS and an authenticated daemon response.

Ownership for parallel work: protocol code under `src/Xas.Core/Protocol/`; identity/pairing under `src/Xas.Core/Security/`; discovery under `src/Xas.Core/Discovery/`; platform shell under `src/Xas.Daemon/Shell/`. Shared types in `Contracts.cs` are integrator-owned. Agents should add tests in their owned areas and avoid editing project or solution files concurrently.
