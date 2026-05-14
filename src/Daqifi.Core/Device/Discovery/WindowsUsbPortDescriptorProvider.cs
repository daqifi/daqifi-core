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

    // SerialPort.GetPortNames() returns "COM<n>" on Windows, but defend
    // against malformed input reaching the WMI query string by validating
    // the shape and rejecting anything that could close out the WQL literal.
    private static readonly Regex PortNameRegex = new(
        @"^COM\d+$",
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
        // Bail explicitly on null/whitespace rather than relying on the
        // outer try/catch to swallow an NRE from the regex match.
        if (string.IsNullOrWhiteSpace(portName))
            return null;

        // Reject anything that doesn't match COM<n> shape — the WQL string is
        // built by interpolation, so a stray quote would corrupt the query.
        if (!PortNameRegex.IsMatch(portName))
            return null;

        // Match COM-port entities by Caption suffix, e.g. "USB Serial Device (COM9)".
        // Restricting to PNPClass='Ports' skips the rest of the device tree
        // and shrinks WMI's enumeration cost; the result collection itself
        // owns native handles and must be disposed.
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports' AND Caption LIKE '%({portName})%'");
        using var results = searcher.Get();
        foreach (var entity in results)
        {
            using (entity)
            {
                var deviceId = entity["DeviceID"] as string;
                if (string.IsNullOrEmpty(deviceId))
                    continue;

                var match = VidPidRegex.Match(deviceId);
                if (!match.Success)
                    continue;

                var vid = int.Parse(
                    match.Groups["vid"].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                var pid = int.Parse(
                    match.Groups["pid"].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                return new UsbPortDescriptor(vid, pid);
            }
        }
        return null;
    }
}
