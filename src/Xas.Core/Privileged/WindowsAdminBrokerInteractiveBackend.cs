using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Xas.Core.Privileged;

/// <summary>Opens elevated Windows ConPTY sessions through the installed administrator broker.</summary>
public sealed class WindowsAdminBrokerInteractiveBackend : IInteractiveShellBackend
{
    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public async Task<IInteractiveShellSession> StartAsync(bool elevated, short columns, short rows,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The administrator broker is available only on Windows.");
        if (!IsAvailable) throw new PlatformNotSupportedException("Elevated ConPTY requires Windows 10 version 1809 or later.");
        if (!elevated) throw new ArgumentException("The administrator broker backend only accepts elevated sessions.", nameof(elevated));
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));

        var pipe = new NamedPipeClientStream(".", WindowsAdminBrokerClient.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            WindowsAdminBrokerClient.VerifyInstalledBrokerServer(pipe.SafePipeHandle);
            var request = new AdminBrokerRequest(Xas.Core.ShellMode.Interactive, null, null, [], null, columns, rows);
            await AdminBrokerWire.WriteHeaderAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            var status = await AdminBrokerWire.ReadHeaderAsync<AdminBrokerStatus>(pipe, cancellationToken).ConfigureAwait(false);
            if (!status.Ok) throw new UnauthorizedAccessException(status.Error ?? "Administrator broker rejected the interactive session.");
            return new BrokerSession(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class BrokerSession : IInteractiveShellSession
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BrokerInputStream _input;
        private readonly BrokerOutputStream _output;
        private int _disposed;

        public BrokerSession(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _input = new BrokerInputStream(WriteFrameAsync);
            _output = new BrokerOutputStream(pipe, _exit);
        }

        public Stream Input => _input;
        public Stream Output => _output;

        public async ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken)
        {
            if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
            var data = new byte[4];
            BinaryPrimitives.WriteInt16LittleEndian(data, columns);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), rows);
            await WriteFrameAsync(AdminFrameKind.Resize, data, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask WriteFrameAsync(AdminFrameKind kind, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await AdminBrokerWire.WriteFrameAsync(_pipe, kind, data, cancellationToken).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _writeLock.Dispose();
        }

        private sealed class BrokerInputStream(Func<AdminFrameKind, ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeFrame) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                writeFrame(AdminFrameKind.Stdin, buffer, cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class BrokerOutputStream(NamedPipeClientStream pipe, TaskCompletionSource<int> exit) : Stream
        {
            private byte[] _current = [];
            private int _offset;
            private int _ended;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty) return 0;
                while (_offset == _current.Length)
                {
                    if (Volatile.Read(ref _ended) != 0) return 0;
                    var (kind, data) = await AdminBrokerWire.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                    if (kind == AdminFrameKind.Stdout) { _current = data; _offset = 0; continue; }
                    if (kind == AdminFrameKind.Exit && data.Length == 4)
                    {
                        exit.TrySetResult(BinaryPrimitives.ReadInt32LittleEndian(data));
                        Interlocked.Exchange(ref _ended, 1);
                        return 0;
                    }
                    if (kind == AdminFrameKind.Error) throw new IOException(Encoding.UTF8.GetString(data));
                    throw new InvalidDataException("Unexpected administrator broker output frame.");
                }
                var count = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

}
