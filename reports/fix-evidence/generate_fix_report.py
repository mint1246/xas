from html import escape
from pathlib import Path

root = Path(__file__).resolve().parents[2]
cases = [
    ("R1", "CLI stream isolation", "Bound shell output is matched by protocol family and remote session ID. Buffered output keeps its order; failed local subscribers cannot terminate the shared peer.", "LocalIpcShellForwardingTests; two concurrent bound commands in IntegrationTests."),
    ("R2", "Peer shutdown", "Shutdown closes established connections before joining dialers. Each connection also observes lifetime cancellation, including late registration.", "PeerSessionManagerTests: idle outbound lane and already-cancelled connection."),
    ("R3", "Filesystem error semantics", "Filesystem errors carry structured codes. Native operations recover missing-path, access-denied, I/O and unsupported-operation exceptions; legacy text errors remain supported.", "FileSystemTests: real protocol round trips and legacy decoding."),
    ("R4", "Virtual monitor churn", "Input acquisition failures retry while retaining the monitor. Transient topology failures preserve ownership. Driver identities use the device name when hardware IDs are empty; asynchronous removal is confirmed before replacement.", "HandoffLifecycleTests, WindowsTopologyTests and VirtualDisplayTests use fake hardware."),
    ("R5", "Administrator broker lifetime", "Stdin EOF closes the child input while the broker keeps watching for disconnect. Service cancellation and stream failures terminate the owned child process tree.", "BrokerServerTests: EOF with open pipe, disconnect after EOF, and service cancellation."),
    ("R6", "Manual remote mounts", "Explicit mount intent survives automatic-mount settings and reconnects. Missing volumes or revoked access clear that intent.", "RemoteMountManagerTests use fake adapters and cover setting changes, reconnects and disappearance."),
    ("R7", "Durable filesystem flush", "Native flush/sync callbacks wait for fs.flush. Files flush to disk; Linux directories use fsync. Unsupported operations return errors instead of reporting success.", "FileSystemTests and adapter tests; Linux directory flush exercised under WSL."),
    ("R8", "Linux upgrades", "Binaries are staged and atomically replaced. Active daemons restart; inactive units are enabled and started. Failed installation or service operations return failure.", "LinuxInstallerTests run with mocked systemctl: active/inactive units, failed restart/start, and failed staging."),
    ("R9", "Web sessions", "Reloads reuse the browser session and CSRF token. Sessions expire and the store is bounded; opening another browser preserves existing sessions.", "WebSessionTests cover reloads, independent browsers, expiry and eviction."),
    ("R10", "Command close and cleanup", "Bound close requests reach the remote command, local disconnect closes owned sessions, and completed commands release their session resources automatically.", "ShellTests and IPC helper/integration coverage."),
    ("Linux", "Interactive echo and xas -c", "Terminal input bypasses .NET's line reader through a duplicated file descriptor. Client input pumps run on the thread pool so synchronous terminal reads cannot block protocol setup. Size polling uses ioctl. One-shot commands stop stdin forwarding when the child exits, then drain stdout/stderr.", "Linux ShellTests, blocking-input IPC integration, and a real PTY probe: exact input delivery without newline, no local echo, repeated size polling, and terminal restoration."),
]

rows = "".join(
    f'<tr><td><span class="id">{escape(id_)}</span></td><td><strong>{escape(title)}</strong><p>{escape(change)}</p></td><td>{escape(check)}</td></tr>'
    for id_, title, change, check in cases
)
logs = []
for name, title in [("final-build.txt", "Final solution build"), ("windows-tests.txt", "Windows regression suites"), ("windows-integration.txt", "Isolated network integration"), ("linux-tests.txt", "Linux regression suites"), ("linux-terminal.txt", "Linux PTY input and restoration")]:
    path = Path(__file__).parent / name
    if path.exists():
        log = path.read_text(encoding="utf-8-sig").replace("\x1b", "\\x1b")
        logs.append(f'<details><summary>{escape(title)}</summary><pre>{escape(log)}</pre></details>')

document = """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>XAS repair verification — 30 September 2026</title><style>
:root{color-scheme:light;--ink:#182a35;--muted:#52636d;--line:#d6e0e3;--accent:#146e63}
*{box-sizing:border-box}body{margin:0;background:#edf2f2;color:var(--ink);font:16px/1.55 system-ui,sans-serif}
main{max-width:1120px;margin:40px auto;padding:38px;background:#fff;border:1px solid var(--line);border-radius:16px}
.eyebrow{font-size:12px;text-transform:uppercase;letter-spacing:.15em;color:var(--accent);font-weight:700}
h1{font-size:38px;line-height:1.12;margin:12px 0 20px}h2{font-size:23px;margin-top:32px}
p{max-width:900px}a{color:var(--accent)}.checks{display:flex;gap:12px;flex-wrap:wrap;margin:25px 0}.checks div{padding:15px 20px;background:#eff7f4;border:1px solid #cce2d9;border-radius:10px}.checks strong{display:block;font-size:23px}
.context{border-left:4px solid var(--accent);padding:14px 20px;background:#f4f8f8}
table{border-collapse:collapse;width:100%;font-size:14px}th{text-align:left;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.05em}td,th{padding:17px 12px;border-bottom:1px solid var(--line);vertical-align:top}td p{margin:8px 0 0}.id{background:#edf4f4;padding:4px 7px;border-radius:5px;white-space:nowrap}
details{margin:12px 0;border:1px solid var(--line);border-radius:8px;padding:13px 16px}summary{cursor:pointer;font-weight:600}pre{font-size:12px;line-height:1.6;white-space:pre-wrap;overflow-wrap:anywhere;background:#f3f6f7;padding:15px;border-radius:6px}.foot{font-size:13px;color:var(--muted)}
@media(max-width:700px){main{margin:0;padding:22px;border-radius:0}h1{font-size:30px}td,th{padding:12px 5px}table{font-size:12px}}@media print{body{background:#fff}main{border:0;margin:0;padding:0}details{break-inside:avoid}}
</style></head><body><main>
<div class="eyebrow">XAS · code review follow-up · 30 September 2026</div>
<h1>Repair verification</h1>
<p>The ten review findings have source fixes and regression coverage. The Linux interactive-input problem and one-shot command hang are also addressed. Existing workspace changes were preserved; ten <code>gpt-6-luna</code> agents worked on the repairs and tests.</p>
<div class="checks"><div><strong>26</strong>selected Windows suites passed</div><div><strong>11</strong>selected Linux suites passed</div><div><strong>Passed</strong>isolated network integration and Linux PTY probe</div><div><strong>0</strong>build warnings or errors</div></div>
<div class="context"><strong>The installed daemon and administrator broker service remain stopped.</strong><br>These changes have not been deployed to the executable in PATH. Monitor lifecycle checks use fake controllers and routers; the physical driver and live handoff have not been exercised. Network integration disables native KVM and mount orchestration.</div>
<h2>Changes and evidence</h2>
<table><thead><tr><th>Finding</th><th>Resulting behavior</th><th>Verification</th></tr></thead><tbody>__ROWS__</tbody></table>
<h2>Linux terminal diagnosis</h2>
<p>On .NET 10.0.10, <code>Console.OpenStandardInput()</code> selects a line reader for terminal input. The controlled PTY probe reproduced local echo and delayed input until newline. Using a raw duplicated descriptor delivered every byte immediately without local echo. Terminal size polling now uses <code>TIOCGWINSZ</code>, avoiding child processes during the interactive session. The implementation is consistent with the <a href="https://github.com/dotnet/runtime/blob/v10.0.10/src/libraries/System.Console/src/System/ConsolePal.Unix.cs">.NET 10.0.10 Console source</a>.</p>
<p>The one-shot command hang had two additional causes: the backend awaited stdin forwarding after its child exited, and a synchronously blocking terminal read could prevent the client from reaching its output wait. The backend now stops input forwarding and drains output before returning the exit code; clients start input pumps on the thread pool. An IPC regression confirms command completion while a simulated terminal read remains blocked.</p>
<h2>Verification records</h2>
<p>The solution and both daemon targets (<code>win-x64</code>, <code>linux-x64</code>) compiled without warnings or errors. Linux tests ran in the existing KVM-Linux-Build WSL distribution using an official .NET runtime whose SHA-512 matched Microsoft release metadata. Installer tests use a mock service manager; native filesystem mount tests use fake adapters.</p>
__LOGS__
<p class="foot">The <a href="code-review-2026-09-29.html">original review</a> remains a historical snapshot of the pre-fix source. Current regression suites are in <code>tests/</code>; the standalone terminal probe is in <code>tests/Terminal.PtyProbe/</code>. Build outputs, dependency caches and the nested checkout are ignored. Pending source and diagnostic-tool changes in the nested checkout were preserved in commit <code>91232d6</code> on <code>codex/preserved-displayfix</code>.</p>
</main></body></html>"""
(root / "reports" / "fix-verification-2026-09-30.html").write_text(document.replace("__ROWS__", rows).replace("__LOGS__", "".join(logs)), encoding="utf-8")
