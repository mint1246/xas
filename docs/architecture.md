# xas implementation contracts

The user-session daemon owns networking and authorization. Each installation has a persistent certificate identity and a local peer trust store. A connection must mutually authenticate before any service request is accepted. Advertised capabilities describe implementation availability; local peer grants separately determine access.

Projects: `Xas.Core` owns shared types, protocol, identity, and configuration; `Xas.Daemon` hosts services; `Xas.Cli` is the `xas` command. The initial transport is TLS over TCP if current QUIC deployment requirements make it less portable. Protocol version 1 uses bounded binary frames and explicit request IDs. Payloads may be UTF-8 JSON for control records; bulk bytes must stay binary. Unknown capabilities and message versions fail explicitly.

Initial shell modes: `Command` runs through the target OS shell; `Exec` uses executable plus argv; `Interactive` requires a real PTY/ConPTY. No line-at-a-time interactive fallback. Elevation is a separate authorization and remains unavailable until a real platform broker exists.

Ownership for parallel work: protocol code under `src/Xas.Core/Protocol/`; identity/pairing under `src/Xas.Core/Security/`; discovery under `src/Xas.Core/Discovery/`; platform shell under `src/Xas.Daemon/Shell/`. Shared types in `Contracts.cs` are integrator-owned. Agents should add tests in their owned areas and avoid editing project or solution files concurrently.
