using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves USB VID/PID for serial ports from the shared <see cref="WindowsPnpPortMap"/>, which is
/// built from a single WMI <c>Win32_PnPEntity</c> query per discovery pass. Returns null on
/// non-Windows platforms so callers can fall back to a probe-everything strategy.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            // A port the map doesn't list is left unclassified rather than refreshed: the caller
            // probes anything it can't classify, so a miss costs one probe, while refreshing for
            // every miss would put a WMI query back on the per-port path this exists to remove.
            return PnpDeviceIdParser.SelectUsbDescriptor(WindowsPnpPortMap.Shared.GetDeviceIds(portName));
        }
        catch
        {
            // Any WMI error → behave like the no-op provider for this port.
            // The caller will fall through to legacy probe behavior, which
            // is correct for any port we can't classify.
            return null;
        }
    }
}
