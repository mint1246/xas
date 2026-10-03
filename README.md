# xas

A Windows/Linux workstation bridge for remote shells, shared files and clipboards, and keyboard/mouse handoff across machines.

XAS runs a daemon in each user's desktop session. Devices pair through a code comparison, then communicate over pinned mutual TLS. The `xas` CLI and local web UI control persistent peer connections and per-device permissions.

**Status: experimental.** Windows and Linux regression tests cover the managed services and terminal input. Physical Windows/Linux monitor-boundary handoff, Wayland consent, and native filesystem mounts still need end-to-end validation. See the [review and repair report](reports/fix-verification-2026-09-30.html) for verified behavior and remaining limits.

## Features

- Interactive remote shells using Windows ConPTY or a Linux PTY, streamed commands, remote exit codes, and piped stdin.
- File and recursive directory copy with overwrite protection and modification-time preservation.
- Remote filesystem mounts through WinFsp on Windows and FUSE3 on Linux, main-drive sharing, explicit directory exports, removable-volume discovery, and remote eject.
- Plain-text clipboard push, pull, and continuous synchronization.
- Windows virtual-monitor handoff to a Linux desktop through SudoVDA, with **Ctrl+Alt+Esc** for emergency return.
- LAN discovery, per-peer capability grants, a local configuration UI, and background startup services.

## Build

Install the .NET 10 SDK (10.0.300 or a later 10.0.3xx patch). From the repository root:

```sh
dotnet restore Xas.sln --source https://api.nuget.org/v3/index.json
dotnet build Xas.sln --no-restore
```

The repository's `NuGet.Config` clears implicit package sources; the explicit source above restores the WinFsp and FuseDotNet dependencies. Linux interactive shells also need the native PTY helper:

```sh
make -C native/linux-pty
export XAS_LINUX_PTY_HELPER="$PWD/native/linux-pty/xas-linux-pty"
```

This helper needs a C compiler, `make`, and `libutil`. See the [PTY helper documentation](native/linux-pty/README.md) and [Wayland helper documentation](native/linux-wayland-input/README.md) for native components.

## Package and install

On Windows, build self-contained Windows/Linux x64 archives with PowerShell:

```powershell
.\scripts\package.ps1
```

Packages appear under `artifacts/release/`. Linux helpers are included only when separately built and placed in `artifacts/native-linux/` before packaging; inspect the archive before relying on PTY or Wayland support.

Extract the appropriate archive, then install:

| Platform | Command | Startup |
| --- | --- | --- |
| Windows | Run `.\install-windows.ps1` in an elevated PowerShell window | Installs into `%ProgramFiles%\xas`; the `XasAdminBroker` service supervises the daemon in the active user session. Installs the pinned WinFsp runtime when needed. |
| Linux | `sh install-linux.sh` | Installs into `~/.local/bin`; enables or restarts the `xas-daemon.service` systemd user unit when systemd is available. |

Open a new terminal after installation to pick up PATH changes. SudoVDA is installed separately. Linux remote mounts need `fusermount3`, `libfuse3.so.3`, and access to `/dev/fuse`; the installer checks these prerequisites.

On Windows, add `-NoStart` to install or upgrade while leaving the service stopped. Start it later from an elevated PowerShell window with `Start-Service XasAdminBroker`. The service still uses automatic startup on the next boot.

For development, start one daemon per machine with `dotnet run --project src/Xas.Daemon -- serve`, and run the CLI with `dotnet run --project src/Xas.Cli -- <arguments>`. Both daemons need access to their users' desktop sessions for clipboard and input features.

## Pair devices

With both daemons running, use `xas ui` for the local configuration panel, or pair from the CLI:

```text
xas devices
xas pair <device-name-or-short-id> --trust-only
```

Run `xas pair` on the other device to approve the incoming request. Compare the displayed code on both machines and approve only when it matches. `--trust-only` grants no capabilities; grant the desired access on the receiving machine:

```text
xas allow <caller-id> shell
xas allow <caller-id> filesystem
xas allow <caller-id> clipboard
xas allow <caller-id> input
```

The default `xas pair` preset grants Shell, FileSystem, Clipboard, and Input. `--kvm` grants Input only. Windows administrator execution always requires a separate `privilegedshell` grant. `xas deny <id> <capability>` removes a grant; `xas revoke <id>` removes trust and disconnects the peer.

For manual pairing, exchange the IDs and full fingerprints from `xas identity`, then use `xas pair-manual <id> <fingerprint> <host> [control-port]`. `xas endpoint <id> <host> [port]` updates an address. Default ports are TCP 47821 for peer traffic, TCP 47822 for pairing, and UDP 47821 for LAN discovery.

## Use

Select a default peer, or prefix a command with `-d <device-id>`:

```text
xas default <remote-id>
xas ping
xas info
xas
xas -c "uname -a"
xas exec git status
xas cp file.zip :~/Downloads/
xas cp -r ./project :~/projects/
xas clipboard push
xas clipboard pull
xas clipboard sync
xas volumes
xas mounts
xas mount <volume-name-or-id>
xas unmount <volume-name-or-id>
xas eject <volume-name-or-id>
```

Interactive mode requires an attached terminal and restores terminal settings on exit. Commands stream stdout/stderr and return the remote process's exit code. `xas cp` refuses to overwrite existing files unless `-f` is supplied and does not follow symbolic links or reparse points. Configure filesystem sharing and automatic mounting through `xas ui`.

Main-drive sharing and automatic mounting are enabled by default for peers with filesystem permission: Linux exposes `/` as `root`, and Windows exposes its system drive. Removable drives are also shared and mounted automatically. Each behavior can be disabled independently in the Storage tab. Explicit directory exports remain available for narrower sharing. Linux mounts appear at `~/xas/<computer>/<volume>` (for example, `~/xas/DESKTOP-77IJ101/F:`); a short suffix is added only when computer or volume names collide. New mount locations take effect after updating and restarting the daemon.

Use `xas --sudo -c "..."` or `xas exec --sudo <executable> ...` for elevated execution. Linux uses the normal system sudo policy and a PTY for authentication. Windows uses the installed administrator broker and requires `xas allow <caller-id> privilegedshell` on the receiver. `--admin` is an alias for `--sudo`.

Clipboard support transfers plain text up to 256 KiB. Linux needs `wl-copy`/`wl-paste` on Wayland or `xclip`/`xsel` on X11. Images and rich text are not implemented.

## Monitor handoff

Install [SudoVDA](https://github.com/SudoMaker/SudoVDA) on Windows, select a paired Linux device as the default, and grant the Windows device Input access on Linux. The Windows daemon reads the Linux display geometry, creates a matching virtual monitor, and routes keyboard/mouse input when the cursor enters that monitor. Crossing back returns control locally; **Ctrl+Alt+Esc** releases control immediately. `xas display` reports driver and handoff state; `xas input [device-id]` provides manual capture.

The virtual monitor must sit beside a physical monitor without overlapping it. Input-acquisition failures now retry while retaining the monitor, and transient topology failures no longer cause repeated teardown. These lifecycle repairs were tested with fake hardware; the repaired daemon has not been validated against a live SudoVDA driver. A monitor left by a hard-killed daemon may require disabling/re-enabling the display adapter or rebooting.

Linux input depends on the available backend: the uinput helper, the [Wayland portal/libei helper](native/linux-wayland-input/README.md), or the opt-in X11 XTest backend (`XAS_ENABLE_X11_INPUT=1`). Wayland support depends on compositor/portal capabilities and user consent. See [input protocol notes](docs/input-protocol.md) and [platform prerequisites](docs/prerequisites.md).

## Tests and validation

Run individual suites with the custom test runner:

```sh
dotnet run --project tests/Xas.Tests --no-build -- --suite ProtocolTests
dotnet run --project tests/Xas.Tests --no-build -- --suite ShellTests
```

The full runner includes platform-specific tests that access the desktop, clipboard, or native backends. TLS tests need the current user's certificate key store. The [repair report](reports/fix-verification-2026-09-30.html) records 26 selected Windows suites, 11 Linux suites, isolated network integration, and a Linux attached-terminal probe. Native mount adapters and monitor lifecycle were checked with fakes; those checks do not establish physical desktop interoperability.

See [tests/Terminal.PtyProbe](tests/Terminal.PtyProbe/README.md) for the Linux raw-input reproduction and [reports/README.md](reports/README.md) for historical review evidence. Long-lived shell/input reconnection after sleep, transfer resume, clipboard images, and broader physical-platform validation remain outstanding.
