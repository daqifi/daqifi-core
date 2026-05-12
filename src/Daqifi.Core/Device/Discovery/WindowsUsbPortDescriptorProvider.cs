using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves USB VID/PID for serial ports via WMI <c>Win32_PnPEntity</c>.
/// Uses <see cref="System.Management"/>; returns null on non-Windows
/// platforms so callers can fall back to a probe-everything strategy.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    // Win32_PnPEntity DeviceID for USB-attached COM ports follows the form
    // USB\VID_XXXX&PID_XXXX&...\<serial>. Each XXXX is 4 hex chars. We
    // match case-insensitively because some drivers report "Vid_" / "Pid_".
    private static readonly Regex VidPidRegex = new(
        @"VID_(?<vid>[0-9A-F]{4}).*PID_(?<pid>[0-9A-F]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            return QueryWmi(portName);
        }
        catch
        {
            // Any WMI error → behave like the no-op provider for this port.
            // The caller will fall through to legacy probe behavior, which
            // is correct for any port we can't classify.
            return null;
        }
    }

    private static UsbPortDescriptor? QueryWmi(string portName)
    {
        // Match COM-port entities by Caption suffix, e.g. "USB Serial Device (COM9)".
        // Avoids enumerating every PnP device on the system. Use a parameterized
        // LIKE pattern; no user input crosses the WMI boundary.
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT DeviceID FROM Win32_PnPEntity WHERE Caption LIKE '%({portName})%'");
        foreach (var entity in searcher.Get())
        {
            using (entity)
            {
                var deviceId = entity["DeviceID"] as string;
                if (string.IsNullOrEmpty(deviceId))
                    continue;

                var match = VidPidRegex.Match(deviceId);
                if (!match.Success)
                    continue;

                var vid = int.Parse(match.Groups["vid"].Value, System.Globalization.NumberStyles.HexNumber);
                var pid = int.Parse(match.Groups["pid"].Value, System.Globalization.NumberStyles.HexNumber);
                return new UsbPortDescriptor(vid, pid);
            }
        }
        return null;
    }
}
