using System.Runtime.InteropServices;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Picks the platform-appropriate <see cref="IUsbPortDescriptorProvider"/>:
/// Windows → WMI, Linux → sysfs, others → null fallback (legacy probe-all).
/// </summary>
internal static class UsbPortDescriptorProviderFactory
{
    public static IUsbPortDescriptorProvider CreateForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The WindowsUsbPortDescriptorProvider type is annotated
            // [SupportedOSPlatform("windows")]; constructor only runs after
            // the runtime check above, so the analyzer warning is suppressed.
#pragma warning disable CA1416
            return new WindowsUsbPortDescriptorProvider();
#pragma warning restore CA1416
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxUsbPortDescriptorProvider();
        }

        return NullUsbPortDescriptorProvider.Instance;
    }
}
