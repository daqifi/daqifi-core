using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves USB VID/PID for serial ports from the shared <see cref="WindowsPnpPortMap"/>, which is
/// built from a single WMI <c>Win32_PnPEntity</c> query per discovery pass. Returns null on
/// non-Windows platforms so callers can fall back to a probe-everything strategy.
/// </summary>
internal sealed class WindowsUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    [SupportedOSPlatform("windows")]
    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return Resolve(portName, WindowsPnpPortMap.Shared);
    }

    /// <summary>
    /// Resolves the descriptor for <paramref name="portName"/> against <paramref name="map"/>, or
    /// null if the map lists no USB-attached entity for it. Split out from
    /// <see cref="GetDescriptor"/> — which adds the Windows platform gate and supplies the real
    /// <see cref="WindowsPnpPortMap.Shared"/> — so the lookup can be unit tested against a fake map
    /// on any OS, for the same reason <see cref="LinuxUsbPortDescriptorProvider.Resolve"/> is
    /// exposed. Unlike <see cref="GetDescriptor"/>, this never calls into <c>System.Management</c>
    /// directly and carries no platform attribute of its own — whether the call reaches WMI depends
    /// entirely on the <see cref="WindowsPnpPortMap"/> passed in, which is what lets a fake one keep
    /// this path off WMI in tests.
    /// </summary>
    internal static UsbPortDescriptor? Resolve(string portName, WindowsPnpPortMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        try
        {
            // A port the map doesn't list is left unclassified rather than refreshed: the caller
            // probes anything it can't classify, so a miss costs one probe, while refreshing for
            // every miss would put a WMI query back on the per-port path this exists to remove.
            return PnpDeviceIdParser.SelectUsbDescriptor(map.GetDeviceIds(portName));
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
