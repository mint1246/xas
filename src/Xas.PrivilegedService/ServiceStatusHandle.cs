using Microsoft.Win32.SafeHandles;

namespace Xas.PrivilegedService;

internal sealed class ServiceStatusHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ServiceStatusHandle() : base(true) { }
    protected override bool ReleaseHandle() => true;
}
