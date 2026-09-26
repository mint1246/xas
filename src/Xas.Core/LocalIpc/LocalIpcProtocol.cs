using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xas.Core.Configuration;
using Xas.Core.Services;

namespace Xas.Core.LocalIpc;

/// <summary>Wire values shared by the command line and the per-user daemon endpoint.</summary>
public static class LocalIpcProtocol
{
    public const string Devices = "local.devices";
    public const string Info = "local.info";
    public const string Ping = "local.ping";
    public const string SetDefault = "local.default";
    public const string PairBegin = "local.pair.begin";
    public const string PairList = "local.pair.list";
    public const string PairApprove = "local.pair.approve";
    public const string PairPendingEvent = "local.pair.pending";
    public const string Bind = "local.bind";
    public const string ShellOpen = "local.shell.open";
    public const string ShellStdin = ShellExecWire.Stdin;
    public const string ShellStdout = ShellExecWire.Stdout;
    public const string ShellStderr = ShellExecWire.Stderr;
    public const string ShellExit = ShellExecWire.Exit;
    public const string ShellError = ShellExecWire.Error;
    public const string ShellClose = ShellExecWire.Close;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] EncodeId(uint id)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
        return bytes;
    }

    public static uint DecodeId(byte[] bytes)
    {
        if (bytes.Length != 4) throw new InvalidDataException("Invalid shell session ID.");
        var id = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return id == 0 ? throw new InvalidDataException("Invalid shell session ID.") : id;
    }
}

public sealed record LocalTarget(string? DeviceId);
public sealed record LocalPeerRequest(string? DeviceId, string Method, byte[] Payload);
public sealed record LocalShellOpen(string? DeviceId, ShellRequest Request);
public sealed record LocalPairBegin(string Host, int Port, string? ExpectedDeviceId);
public sealed record LocalPairDecision(string PairingId, bool Approve);
public sealed record LocalPairPending(string PairingId, string DeviceId, string DisplayName,
    string Fingerprint, string Code, DateTimeOffset CreatedAtUtc);

/// <summary>Per-user endpoint. The network peer certificate is never exposed to local clients.</summary>
public static class LocalIpcEndpoint
{
    public static string PipeName
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(user));
            return "xas-" + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
        }
    }

    public static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var directory = !string.IsNullOrWhiteSpace(runtime)
                ? Path.Combine(runtime, "xas")
                : Path.Combine(AppPaths.Root, "run");
            return Path.Combine(directory, "daemon.sock");
        }
    }

    public static async Task<Stream> ConnectAsync(CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.ConnectAsync(3000, token).ConfigureAwait(false); return pipe; }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            }
            if (OperatingSystem.IsLinux())
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
            throw new PlatformNotSupportedException("Local daemon IPC requires Windows or Linux.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            throw new IOException("The local xas daemon is unavailable. Start the per-user daemon and try again.", ex);
        }
    }
}
