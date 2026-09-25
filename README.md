# xas

`xas` is an in-progress Windows/Linux workstation bridge. It has mutually authenticated connections, remote shells, explicit file copy, text clipboard sync, and input control. Source for automatic monitor-boundary handoff and a Windows virtual display driver is present, but the native parts have not been built or tested on a Windows/Linux pair. Progress is tracked in [docs/checklist.md](docs/checklist.md).

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
xas cp file.zip laptop:~/Downloads/
xas cp laptop:~/log.txt .
xas cp -r ./project laptop:~/projects/
xas clipboard push
xas clipboard pull
xas clipboard sync
xas input
```

Use `xas -d <id>` for an interactive shell on a specific peer, or add `-c "..."` or `exec ...` for one-shot work. `xas endpoint <id> <host> [port]` updates a changed address. `xas revoke <id>` removes local trust, grants, and endpoint. The daemon listens on TCP port 47821 by default; LAN multicast discovery also uses UDP port 47821. Network and host firewall rules must allow the needed ports. Interactive mode requires an attached terminal. The CLI restores its terminal mode when the session ends.

Command output is streamed as binary frames. Redirected stdin is forwarded for shell version 2, currently up to 900,000 bytes per command. The CLI returns the remote process exit code. Every remote shell request is checked against the receiver's local permission store.

For `xas cp`, grant the caller separately with `xas allow <caller-id> filesystem` on the remote machine, whether the copy uploads or downloads. The transfer streams file bytes, preserves file modification times, copies directories with `-r`, and refuses to overwrite existing files unless `-f` is supplied. It does not follow symbolic links or reparse points. File copy is explicit; Explorer/FUSE mounts remain pending.

For clipboard commands, grant the caller with `xas allow <caller-id> clipboard` on the receiving machine. `push` and `pull` transfer plain text once; `sync` keeps both text clipboards in step until Ctrl+C. It observes both initial values without replacing either, then transfers subsequent changes. Both sides may run sync; when simultaneous edits conflict, the lexically higher device ID wins. Sync retries dropped connections. Text is limited to 256 KiB. On Linux it needs `wl-copy`/`wl-paste` in a Wayland session or `xclip`/`xsel` in X11. Images and rich text remain pending.

For automatic handoff, install and extend the XAS virtual display on Windows, set a default paired Linux device, grant the Windows device Input access on Linux with `xas allow <windows-id> input`, and run the daemons in both logged-in user sessions. The Windows daemon watches the native cursor position: crossing into the XAS monitor opens an authorized input session, sends absolute cursor positions to Linux, and crossing back ends the session. **Ctrl+Alt+Esc** is the emergency return chord. The Linux daemon needs the X11 XTest backend (`XAS_ENABLE_X11_INPUT=1`) or the Wayland portal/libei helper (build instructions in [native/linux-wayland-input/README.md](native/linux-wayland-input/README.md)); Wayland prompts for desktop consent. The receiver releases held keys/buttons on disconnect and expires idle sessions after four seconds. Build and installation steps for the virtual display are in [native/windows-virtual-display/README.md](native/windows-virtual-display/README.md). This path still needs native builds and a physical two-machine test. `xas input [device-id]` remains a manual development fallback. See [docs/input-protocol.md](docs/input-protocol.md).

## Current limits

Windows interactive shells pass a networked ConPTY integration test. Linux interactive code is connected to the same protocol, but the native PTY helper could not be built or exercised on this Windows host; compile it on Linux with `make -C native/linux-pty` and put the resulting `xas-linux-pty` on `PATH` or set `XAS_LINUX_PTY_HELPER` to its path. `--sudo` and `--admin` remain unsupported. Discovery can update a stale endpoint after pinned TLS authentication; reconnect for shell and input sessions is pending. The virtual display currently has three fixed modes, so matching Linux resolution and Windows display layout requires manual configuration. Automatic daemon startup, installers, driver signing, physical monitor-boundary validation, filesystem mounts, removable media, clipboard images and rich text, and web UI remain pending.

See [docs/prerequisites.md](docs/prerequisites.md) for platform components and current upstream references.
