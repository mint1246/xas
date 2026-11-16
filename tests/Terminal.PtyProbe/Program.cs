using System.Runtime.InteropServices;
using System.Text;
using Xas.Cli.Terminal;

Console.CancelKeyPress += (_, e) => e.Cancel = true;
using var terminal = TerminalMode.Enter();
using var input = Environment.GetEnvironmentVariable("XAS_TERMINAL_PROBE_USE_CONSOLE_STREAM") == "1"
    ? Console.OpenStandardInput()
    : TerminalMode.OpenInputStream();
using var output = Console.OpenStandardOutput();
Console.WriteLine($"READY {ReadFlags()}");
var inputCompleted = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var reader = Task.Run(ReadInputAsync);
Console.WriteLine($"READ_PENDING {!reader.IsCompleted}");
Console.Out.Flush();

async Task ReadInputAsync()
{
    var buffer = new byte[128];
    while (true)
    {
        var count = await input.ReadAsync(buffer);
        if (count == 0) break;
        var response = "REMOTE:" + Encoding.UTF8.GetString(buffer, 0, count) + "\n";
        await output.WriteAsync(Encoding.UTF8.GetBytes(response));
        if (buffer.AsSpan(0, count).Contains((byte)'!')) break;
    }
    inputCompleted.TrySetResult(true);
}

var iterations = 0;
while (!inputCompleted.Task.IsCompleted && iterations++ < 80)
{
    _ = TerminalMode.CurrentSize();
    Console.WriteLine($"SIZE {iterations} {ReadFlags()}");
    await Task.Delay(250);
}
await inputCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
await reader;
Console.WriteLine($"DONE {ReadFlags()}");
terminal.Dispose();
Console.WriteLine($"RESTORED {ReadFlags()}");

static string ReadFlags()
{
    if (Native.tcgetattr(0, out var termios) != 0)
        throw new InvalidOperationException("tcgetattr failed.");
    return $"echo={(termios.LocalFlags & 0x8) != 0} canon={(termios.LocalFlags & 0x2) != 0}";
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LinuxTermios
{
    public uint InputFlags;
    public uint OutputFlags;
    public uint ControlFlags;
    public uint LocalFlags;
    public byte Line;
    public fixed byte ControlCharacters[32];
    public uint InputSpeed;
    public uint OutputSpeed;
}

internal static class Native
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int tcgetattr(int fd, out LinuxTermios termios);
}
