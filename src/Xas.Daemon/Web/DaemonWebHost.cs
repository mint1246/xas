using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xas.Core;
using Xas.Core.Security;
using Xas.Daemon.Pairing;
using Xas.Daemon.Sessions;

namespace Xas.Daemon.Web;

/// <summary>Loopback-only local management UI. Mutations require a same-origin session and CSRF token.</summary>
public sealed class DaemonWebHost : IAsyncDisposable
{
    private const string CookieName = "xas_local_session";
    private const int MaxRequestBytes = 16 * 1024;
    private readonly PeerSessionManager _sessions;
    private readonly PeerTrustStore _trust;
    private readonly PeerPermissionStore _permissions;
    private readonly PairingService? _pairing;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, string> _csrfBySession = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();
    private Task? _acceptLoop;

    public DaemonWebHost(PeerSessionManager sessions, PeerTrustStore trust, PeerPermissionStore permissions,
        PairingService? pairing = null, int port = 47832)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _pairing = pairing;
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
                    var sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    lock (_sessionGate)
                    {
                        _csrfBySession.Clear();
                        _csrfBySession[sid] = csrf;
                    }
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
                    await RespondJsonAsync(stream, 200, new { peers, pending, capabilities = Enum.GetNames<Capability>() }, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/permission")
                {
                    var body = JsonSerializer.Deserialize<PermissionRequest>(request.Body) ?? throw new InvalidDataException();
                    if (string.IsNullOrWhiteSpace(body.DeviceId) || !Enum.TryParse<Capability>(body.Capability, out var cap) || !Enum.IsDefined(cap))
                    { await RespondAsync(stream, 400, "application/json", "{\"error\":\"invalid request\"}", null, token); return; }
                    _permissions.SetAllowed(body.DeviceId, cap, body.Allowed);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/pairing/decision" && _pairing is not null)
                {
                    var body = JsonSerializer.Deserialize<PairingDecisionRequest>(request.Body) ?? throw new InvalidDataException();
                    await _pairing.ApproveAsync(body.PairingId, body.Approve, token).ConfigureAwait(false);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/api/revoke")
                {
                    var body = JsonSerializer.Deserialize<DeviceRequest>(request.Body) ?? throw new InvalidDataException();
                    _trust.Revoke(body.DeviceId);
                    _permissions.RemovePeer(body.DeviceId);
                    await RespondAsync(stream, 200, "application/json", "{\"ok\":true}", null, token);
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
        lock (_sessionGate)
        {
            if (!_csrfBySession.TryGetValue(sessionId, out var expected)) return false;
            return request.Headers.TryGetValue("x-xas-csrf", out var provided) &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(provided));
        }
    }

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
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>xas local devices</title>
<style>body{font:16px system-ui;max-width:1000px;margin:2rem auto;padding:0 1rem;background:#10151d;color:#e8edf5}h1{font-size:1.6rem}article{background:#1b2430;border-radius:12px;padding:1rem;margin:1rem 0}small{color:#9cacbf}button,select{padding:.4rem;margin:.2rem}button{cursor:pointer}.pending{border-left:4px solid #d9a441;padding-left:1rem}.cap{display:inline-block;margin:.25rem}</style>
<h1>xas devices</h1><p><small>This management page is available only from this computer.</small></p><main id="app">Loading…</main>
<script>const csrf='__CSRF__';async function api(path,data){const r=await fetch(path,{method:data?'POST':'GET',headers:{'X-Xas-CSRF':csrf,...(data?{'Content-Type':'application/json'}:{})},body:data?JSON.stringify(data):undefined});if(!r.ok)throw Error('Request failed');return r.json()}async function refresh(){const d=await api('/api/state');const root=document.querySelector('#app');root.replaceChildren();for(const p of d.peers){const a=document.createElement('article');a.innerHTML='<h2></h2><small></small><div class="caps"></div><button class="revoke">Revoke pairing</button>';a.querySelector('h2').textContent=p.Name+' · '+(p.Online?'online':'offline');a.querySelector('small').textContent=p.DeviceId+' · '+(p.Paired?'paired':'not paired')+(p.Endpoint?' · '+p.Endpoint:'');const caps=a.querySelector('.caps');for(const c of d.capabilities){const label=document.createElement('label');label.className='cap';const input=document.createElement('input');input.type='checkbox';input.checked=p.Permissions[c];input.onchange=()=>api('/api/permission',{deviceId:p.DeviceId,capability:c,allowed:input.checked});label.append(input,document.createTextNode(c));caps.append(label)}a.querySelector('.revoke').onclick=async()=>{await api('/api/revoke',{deviceId:p.DeviceId});refresh()};root.append(a)}for(const q of d.pending){const a=document.createElement('article');a.className='pending';a.innerHTML='<h2></h2><p></p><button>Approve</button> <button>Reject</button>';a.querySelector('h2').textContent='Pairing request: '+q.DisplayName;a.querySelector('p').textContent='Compare code on both devices: '+q.Code+' · '+q.Fingerprint;a.querySelectorAll('button')[0].onclick=()=>decide(q.PairingId,true);a.querySelectorAll('button')[1].onclick=()=>decide(q.PairingId,false);root.prepend(a)}}async function decide(id,approve){await api('/api/pairing/decision',{pairingId:id,approve});refresh()}refresh().catch(e=>document.querySelector('#app').textContent=e.message);setInterval(()=>refresh().catch(()=>{}),3000);</script></html>
""".Replace("__CSRF__", csrf, StringComparison.Ordinal);

    private sealed record Request(string Method, string Path, string Host, string Origin,
        Dictionary<string, string> Headers, string Body);
    private sealed record PermissionRequest(string DeviceId, string Capability, bool Allowed);
    private sealed record PairingDecisionRequest(string PairingId, bool Approve);
    private sealed record DeviceRequest(string DeviceId);
}
