using System.Runtime.InteropServices;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Picks the platform-appropriate <see cref="IUsbLocationProvider"/>: Windows → WMI-resolved
/// <c>DEVPKEY_Device_LocationInfo</c>, others → null fallback (location correlation is
/// unavailable; consumers should treat every device as having no known location).
/// </summary>
internal static class UsbLocationProviderFactory
{
    public static IUsbLocationProvider CreateForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The WindowsUsbLocationProvider type is annotated
            // [SupportedOSPlatform("windows")]; constructor only runs after
            // the runtime check above, so the analyzer warning is suppressed.
#pragma warning disable CA1416
            return new WindowsUsbLocationProvider();
#pragma warning restore CA1416
        }

        return NullUsbLocationProvider.Instance;
    }
}
