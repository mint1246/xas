# xas

`xas` is an in-progress Windows/Linux workstation bridge. It has mutually authenticated connections, remote shells, explicit file copy, text clipboard sync, and input control. Automatic monitor-boundary handoff is implemented on top of the SudoVDA indirect display driver, so the Windows daemon creates and removes its virtual monitor by itself; it has not yet been validated on a physical Windows/Linux pair. Progress is tracked in [docs/checklist.md](docs/checklist.md).

## Build and test

Install the .NET 10 SDK, then run:

```text
dotnet build Xas.sln
dotnet run --project tests/Xas.Tests/Xas.Tests.csproj
```

On Windows, the TLS tests need normal access to the current user's certificate key store. The source uses only the .NET runtime and has no NuGet package dependencies.

## Publish and install

On Windows with the .NET 10 SDK and network access to nuget.org, create self-contained x64 packages for Windows and Linux with:

```powershell
.\scripts\package.ps1
```

The archives are `artifacts/release/xas-win-x64.zip` and `artifacts/release/xas-linux-x64.zip`. Extract the matching archive, then run `install-windows.ps1` in PowerShell or `sh install-linux.sh` on Linux. These scripts install `xas` and `Xas.Daemon` under the user's program/bin directory and add it to that user's PATH; open a new terminal afterward. The current Linux archive also includes the native PTY and Wayland helpers, built on Ubuntu 24.04 x64 in an isolated WSL distro. The Wayland helper needs the `libei1` and `liboeffis1` runtime packages and a compatible desktop portal; X11 input needs `libX11` and `libXtst`. The packages do not install or sign the Windows display driver.

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

Handoff is automatic. Install [SudoVDA](https://github.com/SudoMaker/SudoVDA) on Windows, set a default paired Linux device, grant the Windows device Input access on Linux with `xas allow <windows-id> input`, and run the daemons in both logged-in user sessions. The Windows daemon reads the Linux display's mode over `display.info`, creates a monitor on that adapter at the matching resolution and refresh rate, and extends the desktop beside your physical monitors. No display configuration, environment variable, or per-run setup is required; `xas display` reports the driver, the adapter, and which monitor handoff currently owns. The daemon watches the native cursor position: crossing into the virtual monitor opens an authorized input session, sends absolute cursor positions to Linux, and crossing back ends the session. The monitor identity is derived from the Linux display and its mode, so Windows restores the same arrangement across restarts while a resolution change still takes effect; the monitor is removed when the daemon exits. **Ctrl+Alt+Esc** is the emergency return chord. The Linux daemon needs the X11 XTest backend (`XAS_ENABLE_X11_INPUT=1`) or the Wayland portal/libei helper (build instructions in [native/linux-wayland-input/README.md](native/linux-wayland-input/README.md)); Wayland prompts for desktop consent. It reads the remote display mode with xrandr on X11, or wlr-randr, kscreen-doctor, or Mutter's D-Bus API on Wayland depending on the compositor; GNOME is read over `gdbus`, which ships with GLib and needs no extra package. On a scaled display the virtual monitor matches the logical desktop size rather than the raw panel mode. The receiver releases held keys/buttons on disconnect and expires idle sessions after four seconds. Handoff needs a desktop extended to at least two monitors whose rectangles do not overlap, and it stays inactive while another application, such as Apollo, also owns a virtual monitor on the same adapter. `xas input [device-id]` remains a manual development fallback. See [docs/input-protocol.md](docs/input-protocol.md).

## Current limits

Windows interactive shells pass a networked ConPTY integration test. The Linux PTY helper builds on Ubuntu 24.04 and passes a local framed-protocol smoke test; a networked Windows/Linux shell session still needs physical validation. The Wayland helper builds but has not been tested with a desktop portal. `--sudo` and `--admin` remain unsupported. Discovery can update a stale endpoint after pinned TLS authentication; reconnect for shell and input sessions is pending. The virtual display creates, reconfigures, and removes monitors against SudoVDA, verified locally on a machine with the driver installed. It takes its mode from the Linux display, but the arrangement still has to place the new monitor beside a physical one without overlapping it, and the boundary handoff itself has not been validated on a physical Windows/Linux pair. SudoVDA has no request that lists existing monitors, so a monitor left behind by a hard-killed daemon cannot be identified afterwards; disable and re-enable the SudoVDA display adapter, or reboot, to clear it. Automatic daemon startup, physical monitor-boundary validation, filesystem mounts, removable media, clipboard images and rich text, and web UI remain pending. The PATH installer scripts do not configure daemon startup.

See [docs/prerequisites.md](docs/prerequisites.md) for platform components and current upstream references.
