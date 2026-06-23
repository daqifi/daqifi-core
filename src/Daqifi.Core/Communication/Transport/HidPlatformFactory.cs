using System.Runtime.InteropServices;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Selects the platform-appropriate <see cref="IHidPlatform"/> backend:
/// macOS → native IOKit (<see cref="MacOsHidPlatform"/>), all other platforms →
/// HidSharp (<see cref="HidLibraryPlatform"/>). macOS needs a dedicated backend
/// because HidSharp 2.6.4 enumerates 0 HID devices there (daqifi-core #262).
/// Mirrors <c>UsbPortDescriptorProviderFactory</c>.
/// </summary>
internal static class HidPlatformFactory
{
    public static IHidPlatform CreateForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // MacOsHidPlatform is annotated [SupportedOSPlatform("macos")]; the
            // constructor only runs after the runtime check above, so the platform
            // compatibility analyzer warning is suppressed (matches the Windows
            // path in UsbPortDescriptorProviderFactory).
#pragma warning disable CA1416
            return new MacOsHidPlatform();
#pragma warning restore CA1416
        }

        return new HidLibraryPlatform();
    }
}
