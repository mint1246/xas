using System.Diagnostics;
using System.Threading.Channels;
using Xas.Core;

namespace Xas.Daemon.Shell;

/// <summary>Linux interactive sessions backed by the xas-linux-pty helper.</summary>
public sealed class LinuxPtyBackend : IInteractiveShellBackend
{
    public const string HelperPathEnvironmentVariable = "XAS_LINUX_PTY_HELPER";
    private readonly string _helperPath;

    public LinuxPtyBackend(string? helperPath = null) =>
        _helperPath = string.IsNullOrWhiteSpace(helperPath)
            ? Environment.GetEnvironmentVariable(HelperPathEnvironmentVariable) ?? "xas-linux-pty"
            : helperPath;

    public bool IsAvailable => OperatingSystem.IsLinux() &&
        (File.Exists(_helperPath) || (!Path.IsPathRooted(_helperPath) && FindOnPath(_helperPath)));

    public async Task<IInteractiveShellSession> StartAsync(bool elevated, short columns, short rows,
        CancellationToken cancellationToken)
    {
        if (elevated) throw new NotSupportedException("Elevated interactive shells are not supported.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux PTY sessions are available only on Linux.");
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) throw new FileNotFoundException($"Linux PTY helper '{_helperPath}' was not found. Configure {HelperPathEnvironmentVariable}.", _helperPath);

        var info = new ProcessStartInfo(_helperPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("--cols"); info.ArgumentList.Add(columns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--rows"); info.ArgumentList.Add(rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The Linux PTY helper could not be started.");
            cancellationToken.ThrowIfCancellationRequested();
            return new LinuxPtySession(process);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    private static bool FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        return path.Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, executable)));
    }
}

internal sealed class LinuxPtySession : IInteractiveShellSession
{
    private const int MaxFrame = 65_536;
    private readonly Process _process;
    private readonly Stream _protocolInput;
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readerTask;
    private readonly Task _stderrTask;
    private readonly Task<int> _exitTask;
    private readonly RawOutputStream _rawOutput;
    private readonly FrameInputStream _frameInput;
    private int _inputClosed, _disposed, _terminateSent;

    internal LinuxPtySession(Process process)
    {
        _process = process; _protocolInput = process.StandardInput.BaseStream;
        _rawOutput = new RawOutputStream(this);
        _frameInput = new FrameInputStream(this);
        _stderrTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        _readerTask = ReadFramesAsync();
        _exitTask = WaitForProcessAsync();
    }

    public Stream Input => _frameInput;
    public Stream Output => _rawOutput;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            return WaitWithCancellationAsync(cancellationToken);
        return _exitTask;
    }

    private async Task<int> WaitWithCancellationAsync(CancellationToken token)
    {
        try { return await _exitTask.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { await SendTerminateAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var payload = new byte[] { (byte)(columns >> 8), (byte)columns, (byte)(rows >> 8), (byte)rows };
        await WriteFrameAsync(0x02, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > MaxFrame) throw new ArgumentOutOfRangeException(nameof(payload));
        if (Volatile.Read(ref _inputClosed) != 0) throw new ObjectDisposedException(nameof(Input));
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _inputClosed) != 0) throw new ObjectDisposedException(nameof(Input));
            var header = new byte[] { type, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
            await _protocolInput.WriteAsync(header, token).ConfigureAwait(false);
            if (!payload.IsEmpty) await _protocolInput.WriteAsync(payload, token).ConfigureAwait(false);
            await _protocolInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task SendTerminateAsync()
    {
        if (Interlocked.Exchange(ref _terminateSent, 1) != 0 || Volatile.Read(ref _inputClosed) != 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await WriteFrameAsync(0x03, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private async Task ReadFramesAsync()
    {
        Exception? failure = null;
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            var header = new byte[5];
            while (true)
            {
                var first = await stream.ReadAsync(header.AsMemory(0, 1)).ConfigureAwait(false);
                if (first == 0) break;
                await ReadExactlyAsync(stream, header.AsMemory(1)).ConfigureAwait(false);
                var len = ((uint)header[1] << 24) | ((uint)header[2] << 16) | ((uint)header[3] << 8) | header[4];
                if (len > MaxFrame) throw new InvalidDataException("Linux PTY helper sent an oversized frame.");
                var type = header[0];
                if ((type == 0x81 && (len == 0 || len > 16_384)) || (type == 0x82 && len != 4) || (type == 0xff && len > MaxFrame) || (type != 0x81 && type != 0x82 && type != 0xff))
                    throw new InvalidDataException($"Linux PTY helper sent an invalid frame (type 0x{type:x2}, length {len}).");
                var payload = new byte[(int)len];
                await ReadExactlyAsync(stream, payload).ConfigureAwait(false);
                if (type == 0x81) { await _output.Writer.WriteAsync(payload).ConfigureAwait(false); continue; }
                if (type == 0xff) throw new IOException("Linux PTY helper error: " + System.Text.Encoding.UTF8.GetString(payload));
                _reportedExitCode = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload);
                break;
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { _output.Writer.TryComplete(failure); }
    }

    private int? _reportedExitCode;
    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Linux PTY helper closed a partial frame.");
            buffer = buffer[read..];
        }
    }

    private async Task<int> WaitForProcessAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await _readerTask.ConfigureAwait(false);
        await _stderrTask.ConfigureAwait(false);
        return _reportedExitCode ?? _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await SendTerminateAsync().ConfigureAwait(false);
        try { await _exitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { try { _process.Kill(entireProcessTree: true); } catch { } await _exitTask.ConfigureAwait(false); }
        Interlocked.Exchange(ref _inputClosed, 1);
        await _protocolInput.DisposeAsync().ConfigureAwait(false);
        await _process.StandardOutput.BaseStream.DisposeAsync().ConfigureAwait(false);
        await _process.StandardError.BaseStream.DisposeAsync().ConfigureAwait(false);
        _process.Dispose(); _writeLock.Dispose();
    }

    private sealed class FrameInputStream(LinuxPtySession owner) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => Volatile.Read(ref owner._inputClosed) == 0;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                var size = Math.Min(buffer.Length, MaxFrame);
                await owner.WriteFrameAsync(0x01, buffer[..size], cancellationToken).ConfigureAwait(false);
                buffer = buffer[size..];
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref owner._inputClosed, 1) == 0) owner._protocolInput.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref owner._inputClosed, 1) == 0)
                await owner._protocolInput.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RawOutputStream(LinuxPtySession owner) : Stream
    {
        private byte[]? _current; private int _offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            if (_current is null || _offset == _current.Length)
            {
                try { _current = await owner._output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); _offset = 0; }
                catch (ChannelClosedException ex) { if (ex.InnerException is not null) throw ex.InnerException; return 0; }
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset); _current.AsMemory(_offset, n).CopyTo(buffer); _offset += n; return n;
        }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_current is null || _offset == _current.Length)
            {
                try { _current = owner._output.Reader.ReadAsync().AsTask().GetAwaiter().GetResult(); _offset = 0; }
                catch (ChannelClosedException ex) { if (ex.InnerException is not null) throw ex.InnerException; return 0; }
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset); _current.AsSpan(_offset, n).CopyTo(buffer); _offset += n; return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
