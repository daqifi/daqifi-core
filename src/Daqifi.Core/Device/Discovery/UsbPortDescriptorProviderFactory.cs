using System.Runtime.InteropServices;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Picks the platform-appropriate <see cref="IUsbPortDescriptorProvider"/>:
/// Windows → WMI, Linux → sysfs, macOS → ioreg, others → null fallback (legacy probe-all).
/// </summary>
internal static class UsbPortDescriptorProviderFactory
{
    public static IUsbPortDescriptorProvider CreateForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Only WindowsUsbPortDescriptorProvider.GetDescriptor carries
            // [SupportedOSPlatform("windows")] — the constructor itself is
            // unannotated, so no CA1416 suppression is needed here.
            return new WindowsUsbPortDescriptorProvider();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxUsbPortDescriptorProvider();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsUsbPortDescriptorProvider();
        }

        return NullUsbPortDescriptorProvider.Instance;
    }
}
