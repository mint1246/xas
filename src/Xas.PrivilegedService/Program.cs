using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Xas.PrivilegedService;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly ServiceMainCallback MainCallback = ServiceMain;
    private static readonly ServiceControlHandler ControlCallback = Control;
    private static readonly ManualResetEvent StopEvent = new(false);
    private static ServiceStatusHandle? _statusHandle;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args.Length == 1 && args[0] == "--console")
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; StopEvent.Set(); };
            BrokerServer.RunAsync(StopEvent).GetAwaiter().GetResult();
            return 0;
        }
        var table = new[] { new ServiceTableEntry { Name = "XasAdminBroker", Main = MainCallback }, default };
        return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandler("XasAdminBroker", ControlCallback);
        if (_statusHandle is null || _statusHandle.IsInvalid) return;
        SetStatus(ServiceState.StartPending, 0, 3000);
        try
        {
            SetStatus(ServiceState.Running, ServiceAcceptedControls.Stop | ServiceAcceptedControls.Shutdown, 0);
            Task.WhenAll(BrokerServer.RunAsync(StopEvent), UserDaemonSupervisor.RunAsync(StopEvent))
                .GetAwaiter().GetResult();
            SetStatus(ServiceState.Stopped, 0, 0);
        }
        catch
        {
            SetStatus(ServiceState.Stopped, 0, 0, 1);
        }
    }

    private static void Control(uint control)
    {
        if (control is 1 or 5) { SetStatus(ServiceState.StopPending, 0, 3000); StopEvent.Set(); }
    }

    private static void SetStatus(ServiceState state, ServiceAcceptedControls controls, uint waitHint, uint exitCode = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = 0x10, CurrentState = state, ControlsAccepted = controls,
            Win32ExitCode = exitCode, WaitHint = waitHint
        };
        if (_statusHandle is not null) SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry { public string? Name; public ServiceMainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint ServiceType; public ServiceState CurrentState; public ServiceAcceptedControls ControlsAccepted; public uint Win32ExitCode; public uint ServiceSpecificExitCode; public uint CheckPoint; public uint WaitHint; }
    private enum ServiceState : uint { StartPending = 2, Running = 4, StopPending = 3, Stopped = 1 }
    [Flags] private enum ServiceAcceptedControls : uint { Stop = 1, Shutdown = 4 }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceControlHandler(uint control);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceStatusHandle RegisterServiceCtrlHandler(string name, ServiceControlHandler handler);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(ServiceStatusHandle handle, ref ServiceStatus status);
}
