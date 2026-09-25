using System.Reflection;
using System.Runtime.InteropServices;
using Xas.Daemon.Input;

namespace Xas.Tests;

public static class WindowsInputBackendTests
{
    public static Task RunAsync()
    {
        var map = typeof(WindowsSendInputBackend).GetMethod("HidToScan", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(4, 0x1E, false); // A
        Check(26, 0x11, false); // W
        Check(30, 0x02, false); // 1
        Check(39, 0x0B, false); // 0
        Check(82, 0x48, true); // Up
        Check(228, 0x1D, true); // Right Ctrl
        var nativeInput = typeof(WindowsSendInputBackend).GetNestedType("NativeInput", BindingFlags.NonPublic)!;
        if (Marshal.SizeOf(nativeInput) != 40) throw new Exception("Native SendInput layout is not 40 bytes.");
        return Task.CompletedTask;

        void Check(ushort usage, ushort scan, bool extended)
        {
            var result = ((ushort Scan, bool Extended))map.Invoke(null, [usage])!;
            if (result != (scan, extended))
                throw new Exception($"HID {usage} mapped to {result} rather than ({scan}, {extended}).");
        }
    }
}
