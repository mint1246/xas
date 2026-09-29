# Linux terminal regression probe

This standalone probe links the production `TerminalMode.cs`. It runs in a new PTY with no daemon or service. The Python harness sends bytes without a newline, checks exact delivery and absence of local echo, exercises repeated size polling, and checks restoration of echo/canonical settings.

Publish from the repository root, using an available NuGet source for the Linux runtime packs:

```sh
dotnet publish tests/Terminal.PtyProbe/TerminalPtyProbe.csproj -c Release -o artifacts/terminal-pty-probe -p:RestoreSources=https://api.nuget.org/v3/index.json
```

Then run on Linux:

```sh
python3 tests/Terminal.PtyProbe/pty_input_probe.py artifacts/terminal-pty-probe/TerminalPtyProbe
```

`XAS_TERMINAL_PROBE_USE_CONSOLE_STREAM=1` selects the old Console input path for comparison. It is expected to fail the raw-input checks on .NET 10.0.10.
