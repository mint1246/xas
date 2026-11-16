using System.Diagnostics;
using Xas.Core;

namespace Xas.Daemon.Shell;

/// <summary>Runs one-shot commands using the host OS shell or an executable directly.</summary>
public sealed class ProcessShellBackend : IShellBackend
{
    public bool SupportsInteractive => false;

    public async Task<int> RunAsync(ShellRequest request, Stream stdin, Stream stdout, Stream stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (request.Elevated)
        {
            return await RunElevatedAsync(request, stdin, stdout, cancellationToken)
                .ConfigureAwait(false);
        }

        var startInfo = CreateStartInfo(request);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("The shell process could not be started.");

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var child = (Process)state!;
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }, process);

        using var inputForwardingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = CopyInputAndCloseAsync(
            stdin,
            process.StandardInput.BaseStream,
            inputForwardingCancellation.Token);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr);

        await process.WaitForExitAsync().ConfigureAwait(false);
        // The caller's input stream may remain open after a one-shot child exits (for
        // example, the streaming RPC channel). Stop only input forwarding here; stdout
        // and stderr still need to drain to preserve all output produced by the child.
        inputForwardingCancellation.Cancel();
        await inputTask.ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task<int> RunElevatedAsync(
        ShellRequest request,
        Stream stdin,
        Stream stdout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException(
                "Elevated shell execution is supported only on Linux, where sudo can authenticate through a PTY.");
        }

        await using var pty = await new LinuxPtyBackend()
            .StartCommandAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using var inputForwardingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ptyInputTask = CopyPtyInputAsync(
            stdin,
            pty,
            inputForwardingCancellation.Token);
        var outputTask = pty.Output.CopyToAsync(stdout, cancellationToken);
        try
        {
            var exitCode = await pty.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            inputForwardingCancellation.Cancel();
            try
            {
                await ptyInputTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (inputForwardingCancellation.IsCancellationRequested)
            {
            }

            await outputTask.ConfigureAwait(false);
            return exitCode;
        }
        catch
        {
            inputForwardingCancellation.Cancel();
            try
            {
                await ptyInputTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await outputTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            throw;
        }
        finally
        {
            inputForwardingCancellation.Cancel();
        }
    }

    private static ProcessStartInfo CreateStartInfo(ShellRequest request)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory
        };

        switch (request.Mode)
        {
            case ShellMode.Interactive:
                throw new NotSupportedException(
                    "Interactive shell sessions require a PTY or ConPTY backend.");
            case ShellMode.Command:
                ConfigureCommandStartInfo(info, request);
                break;
            case ShellMode.Exec:
                ConfigureExecutableStartInfo(info, request);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), "Unknown shell mode.");
        }

        return info;
    }

    private static void ConfigureCommandStartInfo(ProcessStartInfo startInfo, ShellRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException(
                "A command string is required for Command mode.",
                nameof(request));
        }

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(request.Command);
            return;
        }

        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(request.Command);
    }

    private static void ConfigureExecutableStartInfo(
        ProcessStartInfo startInfo,
        ShellRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            throw new ArgumentException(
                "An executable is required for Exec mode.",
                nameof(request));
        }

        startInfo.FileName = request.Executable;
        foreach (var argument in request.Arguments ?? Array.Empty<string>())
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static async Task CopyInputAndCloseAsync(Stream source, Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation kills the child; its input pipe is no longer needed.
        }
        finally
        {
            await destination.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CopyPtyInputAsync(Stream source, LinuxPtySession destination,
        CancellationToken cancellationToken)
    {
        await source.CopyToAsync(destination.Input, cancellationToken).ConfigureAwait(false);
        await destination.CompleteInputAsync(cancellationToken).ConfigureAwait(false);
    }
}
