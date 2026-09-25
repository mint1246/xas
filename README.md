# xas

`xas` is an in-progress Windows/Linux workstation bridge. The working vertical slice is a mutually authenticated device connection, one-shot remote commands, and interactive shells backed by Windows ConPTY or the Linux PTY helper. The larger KVM, mount, clipboard, and control-panel experience is tracked in [docs/checklist.md](docs/checklist.md).

## Build and test

Install the .NET 10 SDK, then run:

```text
dotnet build Xas.sln
dotnet run --project tests/Xas.Tests/Xas.Tests.csproj
```

On Windows, the TLS tests need normal access to the current user's certificate key store. The source uses only the .NET runtime and has no NuGet package dependencies.

## Pair two devices

Run `dotnet run --project src/Xas.Cli -- <arguments>` during development, or publish the CLI as `xas`. On **both** machines, run `xas identity` and exchange the displayed device IDs and full fingerprints over a trusted channel.

On machine A, run `xas pair <B-id> <B-fingerprint> <B-host>`. On machine B, run `xas pair <A-id> <A-fingerprint> <A-host>`. Both commands display the same six-digit code if the fingerprints match. Compare the code on both screens, then type `YES` at each prompt. Pairing grants trust only; it does not grant shell access.

On the machine that will receive shell requests, run `xas allow <caller-id> shell`. Start its daemon with `dotnet run --project src/Xas.Daemon -- serve`. On the caller, run:

```text
xas default <remote-id>
xas ping
xas info
xas -c "uname -a"
xas exec git status
xas
```

Use `xas -d <id>` for an interactive shell on a specific peer, or add `-c "..."` or `exec ...` for one-shot work. `xas endpoint <id> <host> [port]` updates a changed address. `xas revoke <id>` removes local trust, grants, and endpoint. The daemon listens on TCP port 47821 by default; LAN multicast discovery also uses UDP port 47821. Network and host firewall rules must allow the needed ports. Interactive mode requires an attached terminal. The CLI restores its terminal mode when the session ends.

Command output is streamed as binary frames. Redirected stdin is forwarded for shell version 2, currently up to 900,000 bytes per command. The CLI returns the remote process exit code. Every remote shell request is checked against the receiver's local permission store.

## Current limits

Windows interactive shells pass a networked ConPTY integration test. Linux interactive code is connected to the same protocol, but the native PTY helper could not be built or exercised on this Windows host; compile it on Linux with `make -C native/linux-pty` and put the resulting `xas-linux-pty` on `PATH` or set `XAS_LINUX_PTY_HELPER` to its path. `--sudo` and `--admin` remain unsupported. Discovery can update a stale endpoint after pinned TLS authentication; long-lived reconnect state is pending. Automatic daemon startup, installers, input handoff, virtual display, filesystem mounts, file transfer, clipboard, and web UI are pending.

See [docs/prerequisites.md](docs/prerequisites.md) for platform components and current upstream references.
