using System.Reflection;
using Xas.Input;

static void Check(ushort scan, bool extended, ushort expected)
{
    var method = typeof(WindowsInputCapture).GetMethod("ScanToHid", BindingFlags.NonPublic | BindingFlags.Static)!;
    var actual = (ushort)method.Invoke(null, [scan, extended])!;
    if (actual != expected) throw new Exception($"scan {scan:X2}/{extended} mapped to {actual:X2}, expected {expected:X2}");
}

Check(0x1E, false, 0x04); // A
Check(0x2C, false, 0x1D); // Z
Check(0x11, false, 0x1A); // W
Check(0x02, false, 0x1E); // 1
Check(0x0B, false, 0x27); // 0
Check(0x1D, false, 0xE0); // Left Ctrl
Check(0x1D, true, 0xE4);  // Right Ctrl
Check(0x48, true, 0x52);  // Up arrow
Check(0x53, true, 0x4C);  // Delete
Check(0x7F, false, 0);    // Unknown scan code is ignored
if (WindowsInputCapture.IsAvailable != OperatingSystem.IsWindows()) throw new Exception("Platform availability mismatch.");
Console.WriteLine("Passed Windows input mapping checks.");
