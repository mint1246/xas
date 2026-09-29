from pathlib import Path
from html import escape

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'reports' / 'code-review-2026-09-29.html'

findings = [
    dict(id='R10', p='P1', area='Commands', title='Forward bound CLI command-close requests to the remote session',
         state='Static end-to-end request trace',
         sources=[('src/Xas.Cli/LocalDaemonClient.cs', 107, 131), ('src/Xas.Cli/LocalDaemonClient.cs', 153, 164), ('src/Xas.Core/LocalIpc/LocalIpcProtocol.cs', 44, 46), ('src/Xas.Daemon/LocalIpc/LocalIpcServer.cs', 131, 143), ('src/Xas.Daemon/Shell/StreamingCommandManager.cs', 36, 48)],
         trigger='Finish or cancel a one-shot command run by LocalDaemonClient, which opens shell.exec.open after local.bind.',
         problem='The CLI sends shell.exec.close during cleanup. LocalIpcProtocol.ShellClose is the same method string, so the server intercepts it before the bound-session forwarding path. That branch only checks openShells, which is populated by local.shell.open. The current CLI uses the other open path, leaving that dictionary empty. The server therefore returns success without asking the peer to close the command.',
         impact='Cancellation can leave a remote command running. Completed command sessions and their input buffers remain in StreamingCommandManager until the shared peer lane disconnects, causing accumulation across routine CLI use.',
         fix='Track bound commands and forward their close requests with the correct remote ID. Distinguish local remapped closes from raw bound closes, and close owned commands when a local IPC connection disappears.',
         test='Open a command through local.bind plus shell.exec.open, send shell.exec.close, and assert the remote close handler is invoked. Verify cancellation terminates a live fake command and repeated completed commands do not retain sessions.',
         evidence='The same close method is used by the CLI and the remapped IPC branch. The forwarding fallback is below that unconditional successful response; the remote manager removes and cancels sessions only when it receives close or is itself disposed.'),
    dict(id='R1', p='P1', area='Commands', title='Isolate shell events by the command that owns them',
         state='Reproduced forwarding defect; primary CLI path traced',
         sources=[('src/Xas.Daemon/LocalIpc/LocalIpcServer.cs', 100, 106), ('src/Xas.Daemon/LocalIpc/LocalIpcServer.cs', 434, 448), ('src/Xas.Cli/LocalDaemonClient.cs', 179, 186)],
         trigger='Run two CLI commands against the same peer at the same time, or run a command while another shell is producing output.',
         problem='Every bound IPC client subscribes to all PeerSession.MessageReceived events, without filtering the lane or command stream. LocalDaemonClient throws on a different stream ID. The alternative local.shell.open forwarder also accepts every shell stream and rewrites it to its own local ID, including another command’s exit event.',
         impact='Ordinary concurrent commands can disconnect a CLI, mix output, or finish with another command’s exit code. A forwarding failure propagates into the shared peer receive loop and can affect other users of that lane.',
         fix='Track which remote stream IDs each IPC client owns. Filter lane, method, and stream before forwarding; filter buffered early events after the open response resolves the ID. Keep local-client send failures from terminating a shared remote lane.',
         test='Open two commands on one peer, interleave stdout and different exit codes, and verify that each client receives only its own events. Disconnect one client while the other continues.',
         evidence='The isolated harness fed exit event 102 to a forwarder owning stream 101. It forwarded that exit as local stream 1. The main CLI uses the unfiltered bound handler shown above.'),
    dict(id='R2', p='P1', area='Connections', title='Close outbound lanes before waiting for dialers during shutdown',
         state='Reproduced with an in-memory connection',
         sources=[('src/Xas.Daemon/Sessions/PeerSessionManager.cs', 500, 510), ('src/Xas.Daemon/Sessions/PeerSessionManager.cs', 616, 633)],
         trigger='Stop or dispose a session manager while an outbound managed lane remains connected to an idle peer.',
         problem='DisposeAsync cancels _shutdown and then awaits all _dialers before disposing _connections. RunConnectionAsync waits on protocol.Completion without linking that receive loop to the supplied token. Cancellation alone therefore does not close an established outbound lane.',
         impact='Graceful daemon shutdown can wait indefinitely for the other machine to disconnect. Cleanup and restart depend on forceful process termination.',
         fix='Dispose the tracked connections before joining dialers, or register connection disposal against the lane lifetime token. Ensure handshakes and established reads both observe shutdown.',
         test='Establish a managed outbound lane whose peer remains idle, cancel shutdown, and assert bounded disposal without requiring remote EOF.',
         evidence='The harness invoked the actual RunConnectionAsync with a synthetic realtime handshake, tracked the resulting task as a dialer, and called DisposeAsync. Both tasks remained pending after cancellation; synthetic EOF released disposal.'),
    dict(id='R3', p='P1', area='Filesystem', title='Preserve filesystem error codes across the RPC boundary',
         state='Windows mapping reproduced; Linux mapping traced',
         sources=[('src/Xas.Core/Protocol/MultiplexedProtocolPeer.cs', 156, 172), ('src/Xas.Core/Protocol/MultiplexedProtocolPeer.cs', 192, 196), ('src/Xas.Daemon/FileSystem/Mount/WinFspRemoteFileSystemMountAdapter.cs', 562, 579), ('src/Xas.Daemon/FileSystem/Mount/LinuxFuseRemoteFileSystemMountAdapter.cs', 600, 617)],
         trigger='A native mounted-volume operation asks the peer for a missing file, a forbidden path, or an already existing destination.',
         problem='The server encodes only exception text. The client receives RemoteProtocolException, so the native adapters cannot match FileNotFoundException, UnauthorizedAccessException, or IOException. They fall through to a generic I/O error. The friendly-message branches do not apply because RemoteProtocolException is not an IOException.',
         impact='Missing-path lookups report disk I/O failure instead of “not found.” This breaks normal create/replace workflows on native mounts and loses useful access-denied and conflict semantics.',
         fix='Add structured filesystem error codes to error responses and translate them at the filesystem client boundary. Map those codes to NTSTATUS and errno instead of parsing localized exception text.',
         test='Exercise missing paths, denied writes, and destination collisions through a real protocol peer and both native callback mappings; direct fake-backend exceptions do not cover the wire behavior.',
         evidence='The same missing-file condition mapped to 0xC0000034 locally and 0xC00000E9 after the RPC error wrapper. FUSE’s equivalent fallback is visible in source; invoking its native type on Windows was skipped because initialization needs libc.'),
    dict(id='R4', p='P1', area='Monitor', title='Keep persistent input failures from recreating the monitor forever',
         state='Static control-flow finding; no live display reproduction',
         sources=[('src/Xas.Input/HotInputRouter.cs', 69, 76), ('src/Xas.Input/WindowsMonitorHandoff.cs', 71, 79), ('src/Xas.Daemon/WindowsKvmCoordinator.cs', 41, 67)],
         trigger='The peer supplies display metadata but repeatedly rejects input.open—for example, its input backend is unavailable, activation fails, or another peer holds the lease.',
         problem='The monitor is attached before the router requests the input lease. A RemoteProtocolException escapes the router and handoff; disposing the handoff controller removes its monitor. The coordinator then creates a fresh handoff. The current uncommitted change adds a two-second pause but keeps retrying indefinitely.',
         impact='A stable peer-side input error can still cause repeated desktop monitor addition/removal. This is a concrete path compatible with the reported deployed symptom, but the deployed root cause was not established.',
         fix='Handle input acquisition failures inside the router/handoff while retaining the attached display. Gate handoff on compatible input capabilities, classify persistent failures, and suspend retries until a relevant state change rather than rebuilding the monitor.',
         test='Use injected display and input services: return valid display metadata and reject input.open repeatedly. Assert one monitor attachment, no repeated detach/add, a visible error, and recovery when the rejection clears.',
         evidence='InputControlService.Acquire can reject an unavailable backend or occupied lease independently of display.info. The coordinator restarts completed handoffs regardless of the failure category. No daemon, service, hooks, or monitor IOCTLs were started during review.'),
    dict(id='R5', p='P1', area='Privileged service', title='Observe cancellation after elevated commands finish receiving stdin',
         state='Static control-flow finding',
         sources=[('src/Xas.PrivilegedService/BrokerServer.cs', 78, 89), ('src/Xas.PrivilegedService/BrokerServer.cs', 348, 362), ('src/Xas.PrivilegedService/Program.cs', 32, 37)],
         trigger='Start a long-running elevated command with empty or finite stdin, then cancel the CLI or stop the background service.',
         problem='ReceiveInputAsync stops reading the pipe on EndStdin. After that, disconnects cannot reach its IOException branch that kills the child. HandleAsync awaits process.WaitForExitAsync without a stop token and the broker accepts one connection at a time. The service stop event is checked only outside the active handler.',
         impact='The elevated child can keep running after cancellation, block all later administrator commands, and hold service shutdown in StopPending. Killing the daemon does not reliably release a broker request whose stdin reader has already exited.',
         fix='Give each broker request a lifetime linked to service stop and pipe disconnect, independent of stdin EOF. Terminate the owned process tree on cancellation and bound cleanup. Continue observing the connection after closing child stdin.',
         test='Run a synthetic long-lived child with stdin already closed, cancel the client and service separately, and verify child termination, bounded shutdown, and acceptance of the next request.',
         evidence='The source explicitly breaks the input loop at EndStdin and waits for the child with no cancellation. This was not exercised against the stopped installed service.'),
    dict(id='R6', p='P2', area='Filesystem', title='Preserve manually mounted exports during automatic reconciliation',
         state='Reproduced with fake native adapters',
         sources=[('src/Xas.Daemon/FileSystem/Mount/RemoteMountManager.cs', 87, 101), ('src/Xas.Daemon/FileSystem/Mount/RemoteMountManager.cs', 248, 257)],
         trigger='Manually mount an explicitly exported directory, then receive a session-change or remote-volume-change event.',
         problem='MountVolumeAsync accepts any reported volume, including exports. ReconcileAsync builds its desired set from removable volumes only, then removes every mounted volume absent from that set. It does not record whether a mount was manual or automatic.',
         impact='A successfully mounted export disappears on the next reconciliation even though the peer and export are still available. Disabling automatic mounts also enters an unconditional unmount path for manually mounted volumes.',
         fix='Track mount intent separately. Preserve manual mounts while their volume remains available and permitted; apply the removable-only desired set and automatic-mount toggle only to automatic mounts.',
         test='Manually mount an export, reconcile an unchanged online peer, and assert that it remains mounted. Repeat with automatic removable mounting disabled.',
         evidence='The harness mounted export-documents using a fake adapter, observed one mount, and directly invoked the actual reconciler with that same available export. The mount count became zero and the adapter was unmounted.'),
    dict(id='R7', p='P2', area='Filesystem', title='Perform remote durable flushes before reporting sync success',
         state='Static data-flow finding',
         sources=[('src/Xas.Daemon/FileSystem/Mount/LinuxFuseRemoteFileSystemMountAdapter.cs', 552, 555), ('src/Xas.Daemon/FileSystem/Mount/WinFspRemoteFileSystemMountAdapter.cs', 319, 329), ('src/Xas.Daemon/FileSystem/FileSystemService.cs', 200, 210)],
         trigger='An application calls fsync or flush on a native remote mount before treating a save or transaction as durable.',
         problem='FUSE FSync and FSyncDir return success without contacting the peer. WinFsp Flush only fetches metadata. The filesystem RPC has no durable-flush operation; the server writes and disposes a FileStream without Flush(true).',
         impact='Applications are told their data is synchronized while it can still be in the remote OS cache. A remote crash or power loss can lose data after a successful sync response.',
         fix='Define file and directory sync RPC semantics, perform the corresponding remote durable flush, and propagate failures. Until supported, do not promise a successful durability operation.',
         test='Use a backend that records or fails durable flushes. Verify that native sync reaches it and that failure is returned to the application.',
         evidence='This finding concerns the native fs.* mount API. The separate explicit file-upload commit path does call Flush(true), so it is not included in this defect.'),
    dict(id='R8', p='P2', area='Installation', title='Restart the Linux user daemon when installing an upgrade',
         state='Static installer finding',
         sources=[('scripts/install-linux.sh', 10, 17), ('scripts/install-linux.sh', 51, 72)],
         trigger='Run install-linux.sh while an existing xas-daemon.service is already active.',
         problem='The installer replaces the binaries and reloads the unit, then calls enable --now. Starting an already active unit does not replace its running process; there is no stop, restart, or try-restart step.',
         impact='Installation reports success while the old daemon continues running and the new CLI talks to it. Bug fixes and protocol changes do not take effect until a later restart.',
         fix='Coordinate replacement with the existing user service and explicitly restart it after installation. Verify the resulting running process and handle systems without an available user service manager.',
         test='Install over an active disposable service instance and verify that its PID/build changes and that startup failures fail the installer.',
         evidence='Installer inspection only. No Linux installation or service mutation was performed.'),
    dict(id='R9', p='P2', area='Web UI', title='Avoid invalidating every browser tab when the control page reloads',
         state='Static session-management finding',
         sources=[('src/Xas.Daemon/Web/DaemonWebHost.cs', 100, 111), ('src/Xas.Daemon/Web/DaemonWebHost.cs', 267, 276)],
         trigger='Open the control panel in a second tab, or reload one tab while another remains open.',
         problem='Every GET / clears the entire CSRF session dictionary and issues a new cookie/token pair. Existing pages retain their old embedded CSRF token, while the shared browser cookie changes. Their next state request or mutation fails authorization.',
         impact='Opening or reloading the management UI causes otherwise valid tabs to return 403 until reloaded. An unrelated page load also invalidates a different browser session.',
         fix='Reuse a valid session on page load, or retain bounded independently expiring sessions. Account for cookies shared across tabs so each page can obtain the current CSRF token.',
         test='Open two tabs, reload either, then fetch state and update a harmless setting from the other; both sessions should remain usable.',
         evidence='The dictionary-wide Clear occurs before authorization on GET /. The authorization path requires the page token to match the cookie’s current session.'),
]
findings.sort(key=lambda f: (f['p'], int(f['id'][1:])))

def snippet(path, first, last):
    lines = (ROOT / path).read_text(encoding='utf-8-sig').splitlines()
    code = '\n'.join(f'{i:4}  {lines[i-1]}' for i in range(first, min(last, len(lines)) + 1))
    return f'<div class="source"><a href="../{escape(path)}">{escape(path)}:{first}–{last}</a><pre><code>{escape(code)}</code></pre></div>'

cards = []
for f in findings:
    sources = ''.join(snippet(*s) for s in f['sources'])
    cards.append(f'''<article id="{f['id']}" data-priority="{f['p']}" data-area="{escape(f['area'])}">
      <div class="eyebrow"><span class="badge {f['p'].lower()}">{f['p']}</span><span>{f['id']} · {escape(f['area'])}</span><span class="verification">{escape(f['state'])}</span></div>
      <h2>{escape(f['title'])}</h2><p><b>Trigger.</b> {escape(f['trigger'])}</p>
      <p>{escape(f['problem'])}</p><p><b>Impact.</b> {escape(f['impact'])}</p>
      <div class="fix"><b>Suggested fix.</b> {escape(f['fix'])}</div>
      <p class="regression"><b>Regression check.</b> {escape(f['test'])}</p>
      <details><summary>Evidence and source excerpts</summary><p>{escape(f['evidence'])}</p>{sources}</details></article>''')

rows = ''.join(f'<tr data-priority="{f["p"]}"><td><span class="badge {f["p"].lower()}">{f["p"]}</span></td><td>{escape(f["area"])}</td><td><a href="#{f["id"]}">{f["id"]} · {escape(f["title"])}</a></td></tr>' for f in findings)
areas = ''.join(f'<option>{escape(area)}</option>' for area in sorted({f['area'] for f in findings}))
html = '''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>XAS workspace code review · 29 September 2026</title>
<style>
:root{color-scheme:light;--ink:#182c3c;--muted:#526575;--line:#dce4ea;--paper:#fff;--bg:#f3f6f8;--red:#9f2933;--amber:#805516}
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.65 system-ui,-apple-system,Segoe UI,sans-serif}header{background:#132c40;color:white;padding:45px max(24px,calc((100vw - 1120px)/2)) 38px}.kicker{font-size:12px;letter-spacing:.16em;text-transform:uppercase;color:#aed3e7}h1{font-size:clamp(30px,4vw,46px);line-height:1.18;margin:12px 0 16px;letter-spacing:-.03em}header p{max-width:850px;color:#d5e3ec;margin:10px 0}.meta{font:13px/1.7 ui-monospace,Consolas,monospace;color:#bcd0dc;overflow-wrap:anywhere}main{max-width:1170px;margin:auto;padding:26px 25px 64px}.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin-bottom:24px}.metric{background:white;border:1px solid var(--line);border-radius:12px;padding:17px 20px}.metric strong{display:block;font-size:29px;line-height:1.3}.metric small{color:var(--muted)}section.overview,.notice{background:white;border:1px solid var(--line);padding:24px;border-radius:12px;margin-bottom:22px}.notice{border-left:4px solid #ca9146;background:#fffaf1}.notice h2{margin-top:0}.section-title{font-size:22px;margin:0 0 14px}h2{font-size:23px;line-height:1.35;letter-spacing:-.015em;margin:12px 0 15px}p{margin:12px 0}a{color:#175576;text-underline-offset:3px}code{font-family:ui-monospace,Consolas,monospace;font-size:.9em;overflow-wrap:anywhere}.badge{display:inline-block;border-radius:6px;padding:2px 9px;font-size:12px;font-weight:750;line-height:1.8}.p1{background:#ffe6e7;color:var(--red)}.p2{background:#fff0d8;color:var(--amber)}.table-wrap{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:14px}th{text-align:left;color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.06em}td,th{padding:11px 9px;border-bottom:1px solid var(--line)}td:first-child{width:65px}td:nth-child(2){width:140px}tr:last-child td{border:0}.toolbar{display:flex;gap:12px;align-items:center;flex-wrap:wrap;background:var(--bg);padding:12px 0 18px;position:sticky;top:0;z-index:2}.toolbar input,.toolbar select,.toolbar button{font:inherit;border:1px solid #bfccd6;border-radius:8px;padding:9px 12px;background:white;color:var(--ink)}.toolbar input{flex:1;min-width:190px}.toolbar button{cursor:pointer}#visible-count{font-size:13px;color:var(--muted)}article{background:var(--paper);border:1px solid var(--line);border-radius:12px;padding:26px;margin-bottom:18px;scroll-margin-top:90px}.eyebrow{display:flex;align-items:center;gap:9px;flex-wrap:wrap;font-size:13px;color:var(--muted)}.verification{margin-left:auto;font-size:12px}.fix{background:#eef6f9;border-left:3px solid #458da9;padding:13px 16px;margin-top:18px;border-radius:0 6px 6px 0}.regression{font-size:14px;color:var(--muted)}details{border-top:1px solid var(--line);margin-top:18px;padding-top:12px}summary{cursor:pointer;font-size:14px;font-weight:650;color:#265a73}.source{margin-top:16px;font-size:12px}.source a{overflow-wrap:anywhere}pre{overflow-x:auto;background:#11293a;color:#e6f0f6;border-radius:8px;padding:16px;font-size:12px;line-height:1.65;tab-size:4}.validation{display:grid;grid-template-columns:1fr 1fr;gap:24px}.small{font-size:14px;color:var(--muted)}ul,ol{padding-left:22px}footer{font-size:13px;color:var(--muted);margin-top:24px}.hidden{display:none!important}@media(max-width:700px){header{padding:30px 22px}.metrics{grid-template-columns:1fr 1fr}main{padding:20px 16px}article{padding:20px}.verification{margin-left:0;width:100%}.validation{grid-template-columns:1fr}.toolbar{position:static}td:nth-child(2){width:auto}}@media print{body{background:white;font-size:11px}header{background:white;color:#182c3c;padding:0 0 20px}header p,.meta,.kicker{color:#526575}main{padding:0;max-width:none}.toolbar{display:none}article{break-inside:avoid;border:1px solid #bbb;padding:16px}details:not([open]) .source{display:none}a{color:inherit}.metrics{gap:8px}.metric{padding:10px}h1{font-size:28px}h2{font-size:18px}.fix{background:#f6f6f6}pre{white-space:pre-wrap;color:black;background:#f5f5f5}}
</style></head><body>
<header><div class="kicker">Engineering review · current workspace</div><h1>XAS code review</h1>
<p>Ten actionable findings across command routing, connection shutdown, filesystem mounts, privileged execution, installation, the web UI, and automatic monitor handoff.</p>
<div class="meta">29 September 2026 · Asia/Jerusalem<br>Branch: rework/daemon-peer-sessions · HEAD: c44ce806bc407413942ef35e808f771c71e7396d<br>Scope: current source, including existing uncommitted changes and the new Linux FUSE adapter.</div></header>
<main><div class="metrics"><div class="metric"><strong>10</strong><small>Actionable findings</small></div><div class="metric"><strong>6 P1 / 4 P2</strong><small>Urgent / normal priority</small></div><div class="metric"><strong>17 / 17</strong><small>Selected suites passed</small></div><div class="metric"><strong>0</strong><small>Build warnings and errors</small></div></div>
<section class="overview"><h2 class="section-title">Findings at a glance</h2><p class="small">P1: address before relying on the affected workflow. P2: correctness or reliability issue to schedule next. Severity is separate from whether a live reproduction was performed.</p><div class="table-wrap"><table><thead><tr><th>Priority</th><th>Area</th><th>Finding</th></tr></thead><tbody>ROWS</tbody></table></div></section>
<div class="notice"><h2>Reported virtual-monitor loop</h2><p>The working tree contains partial mitigations: monitor identity now uses GDI device names when hardware IDs are blank, and asynchronous handoff failures now wait two seconds before retrying. Layout-only changes already rebind the existing monitor. <b>The current code still permits endless monitor recreation after a persistent input-open failure (R4).</b></p>
<p>The PATH executable is <code>C:\\Program Files\\xas\\Xas.Daemon.exe</code>. Its ProductVersion advertises <code>1.0.0+c44ce806bc407413942ef35e808f771c71e7396d</code>, matching HEAD. That version label does not prove a clean-source build or inclusion of today’s uncommitted fixes. Its local modification date is 27 September 2026.</p>
<p class="small">The deployed root cause remains unverified. No display driver operations, live input capture, daemon startup, service startup, installation, or native mounts were performed. The service was observed stopped.</p></div>
<div class="toolbar"><input id="search" aria-label="Search findings" placeholder="Search findings or source paths"><select id="priority" aria-label="Filter priority"><option value="">All priorities</option><option>P1</option><option>P2</option></select><select id="area" aria-label="Filter area"><option value="">All areas</option>AREAS</select><button id="expand">Expand evidence</button><button onclick="window.print()">Print / save PDF</button><span id="visible-count">10 findings</span></div>
<div id="findings">CARDS</div>
<section class="overview"><h2 class="section-title">Validation and review boundaries</h2><div class="validation"><div><b>Verified</b><ul><li><code>dotnet build Xas.sln --no-restore -v minimal</code>: succeeded after restoring declared dependencies; zero warnings and errors.</li><li>17 selected suites passed: VirtualDisplay, WindowsTopology, InputControl, WindowsInputBackend, DisplayMetadata, InputWire, Protocol, Cli, FileSystem, WinFspAdapter, FuseCore, RemoteMountManager, Security, Transport, Pairing, Shell, and ClipboardSync.</li><li>Isolated current-source reproductions confirmed shell forwarding, outbound shutdown ordering, native filesystem error mapping, and manual export unmounting.</li></ul></div><div><b>Limits</b><ul><li>This is a general engineering review, not an exhaustive security audit. Source was reviewed across Core, CLI, Daemon, Input, the privileged service, and installation scripts; not every file or native helper was exhaustively inspected.</li><li>The full integration suite was not run because it starts a DaemonHost. Physical monitor/input handoff, Linux FUSE mounting, and elevated service execution remain untested.</li><li>Initial build used stale restore assets. Dependency restore and certificate-backed tests needed elevated sandbox access. The subsequent build and selected test results supersede those environment failures.</li><li>Product source was not edited. Suggested fixes and regression checks are proposals. Build/restore refreshed generated artifacts; the report and isolated harness are added review artifacts.</li></ul></div></div>
<p class="small">Evidence: <a href="review-evidence/Program.cs">isolated reproduction source</a> · <a href="review-evidence/results.txt">reproduction output</a> · <a href="review-evidence/ReviewEvidence.csproj">reproduction project</a>. Run from the workspace with <code>dotnet run --project reports/review-evidence/ReviewEvidence.csproj</code>. The harness uses reflection into current assemblies and synthetic peers/adapters; it starts no daemon/service or hardware operations. Native FUSE mapping cannot initialize on Windows, so that portion is explicitly reported as not run.</p></section>
<footer>Review snapshot: 29 September 2026. Findings refer to this working tree, not just the commit diff. Existing user changes were preserved. Source excerpts are embedded so this HTML remains useful when shared without the repository.</footer>
</main><script>
const cards=[...document.querySelectorAll('#findings article')], search=document.querySelector('#search'), priority=document.querySelector('#priority'), area=document.querySelector('#area');
function filter(){const q=search.value.toLowerCase();let n=0;cards.forEach(c=>{const show=(!priority.value||c.dataset.priority===priority.value)&&(!area.value||c.dataset.area===area.value)&&c.textContent.toLowerCase().includes(q);c.classList.toggle('hidden',!show);if(show)n++});document.querySelector('#visible-count').textContent=n+' finding'+(n===1?'':'s')}
[search,priority,area].forEach(e=>e.addEventListener('input',filter));document.querySelector('#expand').addEventListener('click',e=>{const open=e.target.textContent==='Expand evidence';cards.forEach(c=>c.querySelector('details').open=open);e.target.textContent=open?'Collapse evidence':'Expand evidence'});document.querySelectorAll('a[href^="#R"]').forEach(a=>a.addEventListener('click',()=>{search.value='';priority.value='';area.value='';filter()}));
</script></body></html>'''
html = html.replace('ROWS', rows).replace('AREAS', areas).replace('CARDS', ''.join(cards))
OUT.write_text(html, encoding='utf-8')
print(f'Created {OUT} ({OUT.stat().st_size:,} bytes; {len(findings)} findings)')
