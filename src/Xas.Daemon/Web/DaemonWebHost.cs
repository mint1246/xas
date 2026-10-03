using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Configuration;
using Xas.Core.LocalIpc;
using Xas.Core.Protocol;
using Xas.Core.Security;
using Xas.Daemon.FileSystem.Mount;
using Xas.Daemon.Pairing;
using Xas.Daemon.Sessions;

namespace Xas.Daemon.Web;

/// <summary>Loopback-only local management UI. Mutations require a same-origin session and CSRF token.</summary>
internal sealed class DaemonWebHost : IAsyncDisposable
{
    private const string CookieName = "xas_local_session";
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);
    private readonly PeerSessionManager _sessions;
    private readonly PeerTrustStore _trust;
    private readonly PeerPermissionStore _permissions;
    private readonly PairingService? _pairing;
    private readonly LocalConfiguration _configuration;
    private readonly PeerAdministrationService _peerAdministration;
    private readonly RemoteMountManager? _remoteMounts;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly WebSessionStore _webSessions = new();
    private Task? _acceptLoop;

    public DaemonWebHost(PeerSessionManager sessions, PeerTrustStore trust, PeerPermissionStore permissions,
        LocalConfiguration configuration, PeerAdministrationService peerAdministration,
        PairingService? pairing = null, RemoteMountManager? remoteMounts = null, int port = 47832)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _peerAdministration = peerAdministration ?? throw new ArgumentNullException(nameof(peerAdministration));
        _pairing = pairing;
        _remoteMounts = remoteMounts;
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(cancellationToken);
        Console.WriteLine($"xas local UI: http://127.0.0.1:{Port}/");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (linked.IsCancellationRequested) { break; }
            _ = HandleClientAsync(client, linked.Token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                if (request is null) return;
                var localOrigin = $"http://127.0.0.1:{Port}";
                if (!string.Equals(request.Host, $"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase))
                { await RespondAsync(stream, 403, "text/plain", "Forbidden", null, token); return; }
                if (request.Method == "GET" && request.Path == "/")
                {
                    var sid = GetSessionId(request);
                    var csrf = _webSessions.GetOrCreate(sid);
                    var headers = new Dictionary<string, string> { ["Set-Cookie"] = $"{CookieName}={sid}; HttpOnly; SameSite=Strict; Path=/; Max-Age=1800" };
                    await RespondAsync(stream, 200, "text/html; charset=utf-8", RenderPage(csrf), headers, token);
                    return;
                }
                if (!TryAuthorize(request, localOrigin))
                { await RespondAsync(stream, 403, "application/json", "{\"error\":\"forbidden\"}", null, token); return; }

                if (request.Method == "GET" && request.Path == "/api/state")
                {
                    var trusted = _trust.List().ToDictionary(p => p.DeviceId, StringComparer.Ordinal);
                    var snapshots = _sessions.GetSnapshots().ToDictionary(s => s.DeviceId, StringComparer.Ordinal);
                    var peers = snapshots.Keys.Concat(trusted.Keys).Distinct(StringComparer.Ordinal).Select(id =>
                    {
                        snapshots.TryGetValue(id, out var s);
                        trusted.TryGetValue(id, out var t);
                        return new
                        {
                            DeviceId = id, Name = s?.Name ?? t?.DisplayName ?? id,
                            Online = s?.Online ?? false, RealtimeReady = s?.RealtimeReady ?? false,
                            Endpoint = s?.Endpoint, LastSeen = s?.LastSeen, Paired = t is not null,
                            Permissions = Enum.GetValues<Capability>().ToDictionary(c => c.ToString(), c => _permissions.IsAllowed(id, c))
                        };
                    });
                    var pending = _pairing?.ListPending() ?? [];
                    var capabilities = Enum.GetValues<Capability>()
                        .Where(c => c != Capability.PrivilegedShell || OperatingSystem.IsWindows())
                        .Select(c => c.ToString()).ToArray();
                    var storage = new
                    {
                        NativeMountsAvailable = _remoteMounts?.NativeMountsAvailable ?? false,
                        AutoExposeRemovable = _configuration.AutoExposeRemovable,
                        AutoMountRemoteRemovable = _configuration.AutoMountRemoteRemovable,
                        AutoExposeMainDrive = _configuration.AutoExposeMainDrive,
                        AutoMountRemoteMainDrive = _configuration.AutoMountRemoteMainDrive,
                        Exports = _configuration.FileSystemExports,
                        Mounts = _remoteMounts?.GetSnapshots() ?? []
                    };
                    await RespondJsonAsync(stream, 200, new { peers, pending, capabilities, storage }, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/permission")
                {
                    var body = JsonSerializer.Deserialize<PermissionRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    if (string.IsNullOrWhiteSpace(body.DeviceId) || !Enum.TryParse<Capability>(body.Capability, out var cap) || !Enum.IsDefined(cap))
                    { await RespondAsync(stream, 400, "application/json", "{\"error\":\"invalid request\"}", null, token); return; }
                    _permissions.SetAllowed(body.DeviceId, cap, body.Allowed);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/pairing/decision" && _pairing is not null)
                {
                    var body = JsonSerializer.Deserialize<PairingDecisionRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    var pending = _pairing.ListPending().FirstOrDefault(p => p.PairingId == body.PairingId)
                        ?? throw new InvalidDataException("Pairing request not found or expired.");
                    var preset = body.Preset?.ToLowerInvariant() switch
                    {
                        null or "personal" => PairPermissionPreset.Personal,
                        "kvm" => PairPermissionPreset.Kvm,
                        "none" or "trust-only" => PairPermissionPreset.None,
                        _ => throw new InvalidDataException("Unknown pairing permission preset.")
                    };
                    var paired = await _pairing.ApproveAsync(body.PairingId, body.Approve, token).ConfigureAwait(false);
                    if (body.Approve && !paired)
                    {
                        await RespondAsync(stream, 409, "application/json", "{\"error\":\"remote rejected pairing\"}", null, token);
                        return;
                    }
                    if (paired)
                    {
                        _configuration.UpsertPeer(new ConfiguredPeer(pending.DeviceId, pending.DisplayName, pending.Address, pending.ControlPort));
                        PairPermissionPresets.Apply(_permissions, pending.DeviceId, preset);
                        _sessions.UpdateConfiguredPeers(_configuration.Peers);
                    }
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/revoke")
                {
                    var body = JsonSerializer.Deserialize<DeviceRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    _ = await _peerAdministration.RevokeAsync(body.DeviceId).ConfigureAwait(false);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/storage/settings")
                {
                    var body = JsonSerializer.Deserialize<StorageSettingsRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    _configuration.SetAutoExposeRemovable(body.AutoExposeRemovable);
                    _configuration.SetAutoMountRemoteRemovable(body.AutoMountRemoteRemovable);
                    if (body.AutoExposeMainDrive is { } exposeMain) _configuration.SetAutoExposeMainDrive(exposeMain);
                    if (body.AutoMountRemoteMainDrive is { } mountMain) _configuration.SetAutoMountRemoteMainDrive(mountMain);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/storage/volumes")
                {
                    try
                    {
                        var manager = RequireRemoteMounts();
                        var body = JsonSerializer.Deserialize<DeviceRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                        var volumes = await manager.GetRemoteVolumesAsync(body.DeviceId, token).ConfigureAwait(false);
                        await RespondJsonAsync(stream, 200, volumes, token);
                    }
                    catch (Exception ex) when (IsStorageUserError(ex))
                    { await RespondJsonAsync(stream, 400, new { error = ex.Message }, token); }
                    return;
                }
                if (request.Method == "POST" && request.Path is "/api/storage/mount" or "/api/storage/unmount" or "/api/storage/eject")
                {
                    try
                    {
                        var manager = RequireRemoteMounts();
                        var body = JsonSerializer.Deserialize<StorageVolumeRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                        if (request.Path == "/api/storage/mount")
                        {
                            var mounted = await manager.MountVolumeAsync(body.DeviceId, body.Volume, token).ConfigureAwait(false);
                            await RespondJsonAsync(stream, 200, mounted, token);
                        }
                        else if (request.Path == "/api/storage/unmount")
                        {
                            var unmounted = await manager.UnmountVolumeAsync(body.DeviceId, body.Volume, token).ConfigureAwait(false);
                            await RespondJsonAsync(stream, 200, new { ok = true, wasMounted = unmounted }, token);
                        }
                        else
                        {
                            await manager.EjectVolumeAsync(body.DeviceId, body.Volume, token).ConfigureAwait(false);
                            await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                        }
                    }
                    catch (Exception ex) when (IsStorageUserError(ex))
                    { await RespondJsonAsync(stream, 400, new { error = ex.Message }, token); }
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/storage/export")
                {
                    var body = JsonSerializer.Deserialize<StorageExportRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    try
                    {
                        _configuration.UpsertFileSystemExport(new FileSystemExport(body.Id, body.Path, body.Name, body.ReadOnly));
                        await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                    { await RespondJsonAsync(stream, 400, new { error = ex.Message }, token); }
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/storage/export/remove")
                {
                    var body = JsonSerializer.Deserialize<StorageExportRemoveRequest>(request.Body, RequestJson) ?? throw new InvalidDataException();
                    var removed = _configuration.RemoveFileSystemExport(body.Id);
                    await RespondJsonAsync(stream, 200, new { ok = true, removed }, token);
                    return;
                }
                await RespondAsync(stream, 404, "application/json", "{\"error\":\"not found\"}", null, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or InvalidDataException)
            { }
        }
    }

    private bool TryAuthorize(Request request, string origin)
    {
        if (request.Origin.Length != 0 && !string.Equals(request.Origin, origin, StringComparison.Ordinal)) return false;
        if (request.Method != "GET" && !string.Equals(request.Origin, origin, StringComparison.Ordinal)) return false;
        if (!request.Headers.TryGetValue("cookie", out var cookieHeader)) return false;
        var cookie = cookieHeader.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal));
        if (cookie is null) return false;
        var sessionId = cookie[(CookieName.Length + 1)..];
        return request.Headers.TryGetValue("x-xas-csrf", out var provided) && _webSessions.IsValid(sessionId, provided);
    }

    private static string GetSessionId(Request request)
    {
        if (request.Headers.TryGetValue("cookie", out var header))
        {
            var cookie = header.Split(';', StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal));
            var sid = cookie?[(CookieName.Length + 1)..];
            if (sid is { Length: 64 } && sid.All(Uri.IsHexDigit)) return sid;
        }
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private RemoteMountManager RequireRemoteMounts() => _remoteMounts
        ?? throw new PlatformNotSupportedException("Native remote filesystem mounts are unavailable on this platform.");

    private static bool IsStorageUserError(Exception ex) => ex is IOException or InvalidDataException or
        InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or RemoteProtocolException or TimeoutException;

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(2048);
        var chunk = new byte[1024];
        while (buffer.Count <= MaxRequestBytes)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            var all = buffer.ToArray();
            var headerEnd = FindHeaderEnd(all);
            if (headerEnd < 0) continue;
            var head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split("\r\n", StringSplitOptions.None);
            var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (first.Length != 3 || first[2] != "HTTP/1.1") throw new InvalidDataException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException();
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var length = headers.TryGetValue("content-length", out var len) && int.TryParse(len, out var parsed) ? parsed : 0;
            if (length < 0 || length > 4096 || (headers.ContainsKey("transfer-encoding"))) throw new InvalidDataException();
            var bodyStart = headerEnd + 4;
            if (all.Length < bodyStart + length) continue;
            var path = first[1].Split('?', 2)[0];
            return new Request(first[0], path, headers.GetValueOrDefault("host") ?? "", headers.GetValueOrDefault("origin") ?? "",
                headers, Encoding.UTF8.GetString(all, bodyStart, length));
        }
        throw new InvalidDataException("Request is too large.");
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }

    private static Task RespondJsonAsync(NetworkStream stream, int status, object value, CancellationToken token) =>
        RespondAsync(stream, status, "application/json; charset=utf-8", JsonSerializer.Serialize(value), null, token);

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, string body,
        IReadOnlyDictionary<string, string>? extra, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var headers = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'\r\n");
        if (extra is not null) foreach (var (name, value) in extra) headers.Append(name).Append(": ").Append(value).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string RenderPage(string csrf) => """
<!doctype html>
<html lang="en">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width">
<title>xas control panel</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{font:15px system-ui;margin:0;background:#0c1118;color:#e8edf5}.shell{max-width:1100px;margin:auto;padding:24px}header{display:flex;align-items:center;justify-content:space-between;gap:16px;margin-bottom:18px}h1{font-size:1.65rem;margin:0}h2{font-size:1.15rem;margin:.1rem 0 .35rem}h3{font-size:1rem;margin:.2rem 0 .5rem}.muted,small{color:#94a3b8}.tabs{display:flex;gap:8px}.tabs button{background:#141d29;border:1px solid #273448}.tabs button.active{background:#27364a}.panel{display:none}.panel.active{display:block}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:12px}.card,article{background:#151d28;border:1px solid #253244;border-radius:12px;padding:14px;margin:10px 0}.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.between{justify-content:space-between}.cap{display:inline-flex;gap:4px;align-items:center;margin:.25rem .5rem .25rem 0}.pending{border-left:4px solid #d9a441}.status{min-height:1.4em;margin:8px 0;color:#a7f3d0}.status.error{color:#fca5a5}button,input,select{font:inherit}button{cursor:pointer;padding:7px 10px;border:1px solid #334155;border-radius:8px;background:#202c3c;color:#eef2f7}button:hover{background:#2b3a4f}button.danger{border-color:#7f1d1d}button:disabled{opacity:.45;cursor:not-allowed}input,select{padding:7px 9px;border:1px solid #334155;border-radius:8px;background:#0e1621;color:#eef2f7}input[type=text]{min-width:180px;flex:1}.volume{display:grid;grid-template-columns:minmax(150px,1fr) auto;gap:10px;align-items:center;padding:10px 0;border-top:1px solid #263244}.volume:first-child{border-top:0}.tag{display:inline-block;padding:2px 6px;border-radius:999px;background:#263448;color:#cbd5e1;font-size:.8rem;margin-right:5px}.mountpoint{font-family:ui-monospace,monospace;font-weight:650}.formgrid{display:grid;grid-template-columns:1fr 1fr;gap:8px}.formgrid .wide{grid-column:1/-1}@media(max-width:650px){.shell{padding:14px}.formgrid{grid-template-columns:1fr}.formgrid .wide{grid-column:auto}.volume{grid-template-columns:1fr}}
</style>
<div class="shell">
<header><div><h1>xas control panel</h1><div class="muted">Loopback-only management for this computer.</div></div><div class="tabs"><button id="tab-devices" class="active">Devices</button><button id="tab-storage">Storage</button></div></header>
<div id="status" class="status"></div>
<section id="devices" class="panel active"><div id="device-list">Loading…</div></section>
<section id="storage" class="panel">
  <div class="grid">
    <div class="card"><h2>Storage behavior</h2><label class="row"><input id="auto-expose-main" type="checkbox"> Expose the main drive to permitted peers</label><label class="row"><input id="auto-mount-main" type="checkbox"> Automatically mount remote main drives</label><label class="row"><input id="auto-expose" type="checkbox"> Expose local removable drives to permitted peers</label><label class="row"><input id="auto-mount" type="checkbox"> Automatically mount remote removable drives natively</label><small id="mount-support"></small></div>
    <div class="card"><h2>Current remote mounts</h2><div id="mount-list" class="muted">None.</div></div>
  </div>
  <div class="card"><div class="row between"><div><h2>Remote volumes</h2><small>Choose a peer to query its currently exported volumes.</small></div><div class="row"><select id="storage-peer"></select><button id="load-volumes">Refresh volumes</button></div></div><div id="volume-list" class="muted">Choose a peer.</div></div>
  <div class="card"><h2>Local exports</h2><small>Expose an explicit local directory through the filesystem RPC. Paths must be absolute.</small><div id="export-list"></div><div class="formgrid"><input id="export-id" type="text" placeholder="id, e.g. projects"><input id="export-name" type="text" placeholder="display name"><input id="export-path" class="wide" type="text" placeholder="absolute path"><label><input id="export-ro" type="checkbox"> Read-only</label><button id="add-export">Add / update export</button></div></div>
</section>
</div>
<script>
const csrf='__CSRF__';
let state=null,activeTab='devices',selectedPeer=null;
const volumeCache=new Map(),volumeRequests=new Map();
const $=s=>document.querySelector(s);
function setStatus(message,error=false){const e=$('#status');e.textContent=message||'';e.classList.toggle('error',error)}
async function api(path,data){const r=await fetch(path,{method:data===undefined?'GET':'POST',headers:{'X-Xas-CSRF':csrf,...(data===undefined?{}:{'Content-Type':'application/json'})},body:data===undefined?undefined:JSON.stringify(data)});const text=await r.text();let body=null;try{body=text?JSON.parse(text):null}catch{}if(!r.ok)throw Error(body?.error||text||`Request failed (${r.status})`);return body}
function bytes(n){if(n===null||n===undefined||n<0)return'unknown';const u=['B','KiB','MiB','GiB','TiB'];let v=n,i=0;while(v>=1024&&i<u.length-1){v/=1024;i++}return`${i?v.toFixed(v>=10?0:1):v.toFixed(0)} ${u[i]}`}
function switchTab(name){activeTab=name;$('#devices').classList.toggle('active',name==='devices');$('#storage').classList.toggle('active',name==='storage');$('#tab-devices').classList.toggle('active',name==='devices');$('#tab-storage').classList.toggle('active',name==='storage');if(name==='storage'&&state){renderStorage();loadVolumes()}}
$('#tab-devices').onclick=()=>switchTab('devices');$('#tab-storage').onclick=()=>switchTab('storage');
function renderDevices(){const root=$('#device-list');root.replaceChildren();for(const p of state.peers){const a=document.createElement('article');a.innerHTML='<h2></h2><small></small><div class="caps"></div><button class="revoke danger">Revoke pairing</button>';a.querySelector('h2').textContent=p.Name+' · '+(p.Online?'online':'offline');a.querySelector('small').textContent=p.DeviceId+' · '+(p.Paired?'paired':'not paired')+(p.Endpoint?' · '+p.Endpoint:'');const caps=a.querySelector('.caps');for(const c of state.capabilities){const label=document.createElement('label');label.className='cap';const input=document.createElement('input');input.type='checkbox';input.checked=p.Permissions[c];input.onchange=async()=>{try{await api('/api/permission',{deviceId:p.DeviceId,capability:c,allowed:input.checked});setStatus('Permission updated.')}catch(e){input.checked=!input.checked;setStatus(e.message,true)}};label.append(input,document.createTextNode(c));caps.append(label)}a.querySelector('.revoke').onclick=async()=>{try{await api('/api/revoke',{deviceId:p.DeviceId});await refresh(true);setStatus('Pairing revoked.')}catch(e){setStatus(e.message,true)}};root.append(a)}for(const q of state.pending){const a=document.createElement('article');a.className='pending';a.innerHTML='<h2></h2><p></p><button data-preset="personal">Pair personal device</button> <button data-preset="kvm">KVM only</button> <button data-preset="none">Trust only</button> <button data-reject>Reject</button>';a.querySelector('h2').textContent='Pairing request: '+q.DisplayName;a.querySelector('p').textContent='Compare code on both devices: '+q.Code;a.querySelectorAll('button[data-preset]').forEach(b=>b.onclick=()=>decide(q.PairingId,true,b.dataset.preset));a.querySelector('button[data-reject]').onclick=()=>decide(q.PairingId,false,'none');root.prepend(a)}if(!root.children.length)root.textContent='No paired or discovered devices yet.'}
async function decide(id,approve,preset){try{await api('/api/pairing/decision',{pairingId:id,approve,preset});await refresh(true);setStatus(approve?'Pairing approved.':'Pairing rejected.')}catch(e){setStatus(e.message,true)}}
function renderStorage(){const s=state.storage;$('#auto-expose-main').checked=s.AutoExposeMainDrive;$('#auto-mount-main').checked=s.AutoMountRemoteMainDrive;$('#auto-mount-main').disabled=!s.NativeMountsAvailable;$('#auto-expose').checked=s.AutoExposeRemovable;$('#auto-mount').checked=s.AutoMountRemoteRemovable;$('#auto-mount').disabled=!s.NativeMountsAvailable;$('#mount-support').textContent=s.NativeMountsAvailable?'Native remote mounts are available on this system.':'Native remote mounts are unavailable; install/configure the platform mount provider.';const mounts=$('#mount-list');mounts.replaceChildren();if(!s.Mounts.length)mounts.textContent='No remote volumes mounted.';for(const m of s.Mounts){const d=document.createElement('div');d.className='volume';d.innerHTML='<div><strong></strong><div class="muted meta"></div></div><div class="mountpoint"></div>';d.querySelector('strong').textContent=m.VolumeName+' · '+m.DeviceName;d.querySelector('.meta').textContent=`${m.Kind} · ${m.ReadOnly?'read-only':'read/write'} · ${bytes(m.TotalBytes)} · ${m.FileSystem||'unknown fs'}`;d.querySelector('.mountpoint').textContent=m.MountPoint||'?';mounts.append(d)}const peers=state.peers.filter(p=>p.Paired);const select=$('#storage-peer');const previous=selectedPeer;select.replaceChildren();for(const p of peers){const o=document.createElement('option');o.value=p.DeviceId;o.textContent=p.Name+(p.Online?'':' (offline)');select.append(o)}selectedPeer=peers.some(p=>p.DeviceId===previous)?previous:(peers[0]?.DeviceId||null);if(selectedPeer)select.value=selectedPeer;const exports=$('#export-list');exports.replaceChildren();if(!s.Exports.length){const d=document.createElement('div');d.className='muted';d.textContent='No explicit exports configured.';exports.append(d)}for(const x of s.Exports){const d=document.createElement('div');d.className='volume';d.innerHTML='<div><strong></strong><div class="muted meta"></div></div><button class="danger">Remove</button>';d.querySelector('strong').textContent=x.Name;d.querySelector('.meta').textContent=`${x.Id} · ${x.Path} · ${x.ReadOnly?'read-only':'read/write'}`;d.querySelector('button').onclick=async()=>{try{await api('/api/storage/export/remove',{id:x.Id});await refresh(true);setStatus('Export removed.')}catch(e){setStatus(e.message,true)}};exports.append(d)}}
async function saveStorageSettings(){try{await api('/api/storage/settings',{autoExposeMainDrive:$('#auto-expose-main').checked,autoMountRemoteMainDrive:$('#auto-mount-main').checked,autoExposeRemovable:$('#auto-expose').checked,autoMountRemoteRemovable:$('#auto-mount').checked});await refresh(true);setStatus('Storage settings updated.')}catch(e){setStatus(e.message,true)}}
$('#auto-expose-main').onchange=saveStorageSettings;$('#auto-mount-main').onchange=saveStorageSettings;$('#auto-expose').onchange=saveStorageSettings;$('#auto-mount').onchange=saveStorageSettings;$('#storage-peer').onchange=e=>{selectedPeer=e.target.value;loadVolumes()};
function renderVolumes(volumes){const root=$('#volume-list');root.replaceChildren();if(!volumes.length){root.textContent='This peer is not exposing any volumes.';return}for(const v of volumes){const d=document.createElement('div');d.className='volume';d.innerHTML='<div><strong></strong><div class="muted meta"></div></div><div class="actions"></div>';d.querySelector('strong').textContent=v.Volume.Name;const stateText=v.MountedAt?`mounted at ${v.MountedAt}`:(v.AutoMountSuppressed?'unmounted · auto-remount suppressed':'available');d.querySelector('.meta').textContent=`${v.Volume.Kind} · ${v.Volume.ReadOnly?'read-only':'read/write'} · ${bytes(v.Volume.TotalBytes)} total · ${bytes(v.Volume.FreeBytes)} free · ${v.Volume.FileSystem||'unknown fs'} · ${stateText}`;const actions=d.querySelector('.actions');const mount=document.createElement('button');mount.disabled=!v.MountedAt&&!state.storage.NativeMountsAvailable;mount.textContent=v.MountedAt?'Unmount':'Mount';mount.onclick=()=>storageAction(v.MountedAt?'unmount':'mount',v.Volume.Id);actions.append(mount);if(v.Volume.Kind==='removable'){const eject=document.createElement('button');eject.textContent='Eject';eject.className='danger';eject.onclick=()=>storageAction('eject',v.Volume.Id);actions.append(eject)}root.append(d)}}
async function loadVolumes(force=false){
  const peer=selectedPeer,root=$('#volume-list');
  if(!peer){root.textContent='No paired peer selected.';return}
  const cached=volumeCache.get(peer);
  if(cached){renderVolumes(cached.volumes);if(!force&&Date.now()-cached.time<10000)return}
  else root.textContent='Loading volumes…';
  let request=volumeRequests.get(peer);
  if(!request){request=api('/api/storage/volumes',{deviceId:peer});volumeRequests.set(peer,request)}
  try{const volumes=await request;volumeCache.set(peer,{volumes,time:Date.now()});if(selectedPeer===peer)renderVolumes(volumes)}
  catch(e){if(selectedPeer===peer){if(!cached)root.textContent=e.message;setStatus(e.message,true)}}
  finally{if(volumeRequests.get(peer)===request)volumeRequests.delete(peer)}
}
async function storageAction(action,volume){try{await api('/api/storage/'+action,{deviceId:selectedPeer,volume});await refresh(true);await loadVolumes(true);setStatus(action==='eject'?'Remote media safely ejected.':`Volume ${action}ed.`)}catch(e){setStatus(e.message,true);await refresh(true);await loadVolumes().catch(()=>{})}}
$('#load-volumes').onclick=()=>loadVolumes(true);
$('#add-export').onclick=async()=>{const id=$('#export-id').value.trim(),name=$('#export-name').value.trim(),path=$('#export-path').value.trim();if(!id||!name||!path){setStatus('Export id, name, and absolute path are required.',true);return}try{await api('/api/storage/export',{id,path,name,readOnly:$('#export-ro').checked});$('#export-id').value='';$('#export-name').value='';$('#export-path').value='';$('#export-ro').checked=false;await refresh(true);setStatus('Export saved.')}catch(e){setStatus(e.message,true)}};
async function refresh(forceStorage=false){state=await api('/api/state');renderDevices();if(forceStorage||activeTab==='storage'){renderStorage();if(activeTab==='storage')await loadVolumes()}}
refresh(true).catch(e=>setStatus(e.message,true));setInterval(()=>refresh(false).catch(()=>{}),3000);
</script>
</html>
""".Replace("__CSRF__", csrf, StringComparison.Ordinal);

    private sealed record Request(string Method, string Path, string Host, string Origin,
        Dictionary<string, string> Headers, string Body);
    private sealed record PermissionRequest(string DeviceId, string Capability, bool Allowed);
    private sealed record PairingDecisionRequest(string PairingId, bool Approve, string? Preset);
    private sealed record DeviceRequest(string DeviceId);
    private sealed record StorageSettingsRequest(bool AutoExposeRemovable, bool AutoMountRemoteRemovable,
        bool? AutoExposeMainDrive = null, bool? AutoMountRemoteMainDrive = null);
    private sealed record StorageVolumeRequest(string DeviceId, string Volume);
    private sealed record StorageExportRequest(string Id, string Path, string Name, bool ReadOnly);
    private sealed record StorageExportRemoveRequest(string Id);
}

internal sealed class WebSessionStore
{
    private const int MaxSessions = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal string GetOrCreate(string sessionId) => GetOrCreate(sessionId, DateTimeOffset.UtcNow);

    internal string GetOrCreate(string sessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                _sessions[sessionId] = existing with { ExpiresAt = now + Lifetime };
                return existing.Csrf;
            }
            if (_sessions.Count >= MaxSessions)
                _sessions.Remove(_sessions.MinBy(pair => pair.Value.ExpiresAt).Key);
            var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions[sessionId] = new Entry(csrf, now + Lifetime);
            return csrf;
        }
    }

    internal bool IsValid(string sessionId, string csrf) => IsValid(sessionId, csrf, DateTimeOffset.UtcNow);

    internal bool IsValid(string sessionId, string csrf, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _sessions.TryGetValue(sessionId, out var entry) &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.Csrf), Encoding.ASCII.GetBytes(csrf));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var id in _sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _sessions.Remove(id);
    }

    private sealed record Entry(string Csrf, DateTimeOffset ExpiresAt);
}
