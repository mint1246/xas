using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Xas.PrivilegedService;

/// <summary>
/// Keeps the normal XAS daemon alive inside the active interactive user session. The Windows service itself
/// runs in session 0 as LocalSystem, where desktop input hooks and the clipboard cannot work, so it must never
/// run the user daemon directly in its own session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserDaemonSupervisor
{
    private const uint NoSession = 0xFFFFFFFF;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static Task RunAsync(WaitHandle stop) => Task.Run(() => Run(stop));

    private static void Run(WaitHandle stop)
    {
        Process? daemon = null;
        var daemonSession = NoSession;
        try
        {
            while (!stop.WaitOne(0))
            {
                try
                {
                    var activeSession = WTSGetActiveConsoleSessionId();
                    if (activeSession == NoSession)
                    {
                        StopDaemon(ref daemon);
                        daemonSession = NoSession;
                    }
                    else
                    {
                        if (daemon is not null && (HasExited(daemon) || daemonSession != activeSession))
                            StopDaemon(ref daemon);

                        if (daemon is null)
                        {
                            daemon = FindInstalledDaemon(activeSession);
                            if (daemon is not null) Log($"Adopted Xas.Daemon PID {daemon.Id} in session {activeSession}.");
                            else
                            {
                                daemon = StartDaemon(activeSession);
                                Log($"Started Xas.Daemon PID {daemon.Id} in session {activeSession}.");
                            }
                            daemonSession = activeSession;
                        }
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log($"User daemon supervision failed: {ex.Message}");
                    StopDaemon(ref daemon);
                    daemonSession = NoSession;
                }

                if (stop.WaitOne(PollInterval)) break;
            }
        }
        finally { StopDaemon(ref daemon); }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static Process? FindInstalledDaemon(uint sessionId)
    {
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Xas.Daemon.exe"));
        foreach (var process in Process.GetProcessesByName("Xas.Daemon"))
        {
            try
            {
                if ((uint)process.SessionId != sessionId || process.HasExited) { process.Dispose(); continue; }
                var actual = process.MainModule?.FileName;
                if (actual is not null && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actual), expected))
                    return process;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // A process can disappear between enumeration and inspection. Ignore it and continue.
            }
            process.Dispose();
        }
        return null;
    }

    private static void Log(string message)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "xas");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Process StartDaemon(uint sessionId)
    {
        var daemonPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Xas.Daemon.exe"));
        if (!File.Exists(daemonPath)) throw new FileNotFoundException("The installed XAS daemon is missing.", daemonPath);
        if (!WTSQueryUserToken(sessionId, out var sessionToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain the active desktop user's token.");
        using (sessionToken)
        using (var token = DuplicatePrimaryToken(sessionToken))
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default"
            };
            var command = new StringBuilder($"\"{daemonPath}\" serve");
            if (!CreateEnvironmentBlock(out var environment, token, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not build the desktop user's environment.");
            try
            {
                const uint CreateUnicodeEnvironment = 0x00000400;
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcessAsUser(token, daemonPath, command, IntPtr.Zero, IntPtr.Zero, false,
                        CreateUnicodeEnvironment | CreateNoWindow, environment, AppContext.BaseDirectory,
                        ref startup, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the XAS user-session daemon.");
                try { return Process.GetProcessById((int)info.ProcessId); }
                finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }

    private static SafeAccessTokenHandle DuplicatePrimaryToken(SafeAccessTokenHandle token)
    {
        const uint TokenAllAccess = 0xF01FF;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;
        if (!DuplicateTokenEx(token, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var duplicate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the desktop user's token.");
        return duplicate;
    }

    private static void StopDaemon(ref Process? daemon)
    {
        var current = daemon;
        daemon = null;
        if (current is null) return;
        try
        {
            if (!current.HasExited)
            {
                Log($"Stopping supervised Xas.Daemon PID {current.Id}.");
                current.Kill(entireProcessTree: true);
                current.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        finally { current.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType,
        out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token, string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startup, out ProcessInfo processInfo);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(
        out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
