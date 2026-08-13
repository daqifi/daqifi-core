using System.Globalization;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Reads the USB vendor/product identification embedded in a Windows PnP device instance ID. Kept
/// separate from <see cref="WindowsUsbPortDescriptorProvider"/> (which is Windows-only because it
/// calls into <see cref="System.Management"/>) so this pure string-manipulation logic can be unit
/// tested on any platform without WMI/hardware access.
/// </summary>
internal static class PnpDeviceIdParser
{
    // Win32_PnPEntity DeviceID for USB-attached COM ports follows the form
    // USB\VID_XXXX&PID_XXXX&...\<serial>. Each XXXX is 4 hex chars. We match
    // case-insensitively because some drivers report "Vid_" / "Pid_".
    private static readonly Regex VidPidRegex = new(
        @"VID_(?<vid>[0-9A-F]{4}).*PID_(?<pid>[0-9A-F]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the USB descriptor encoded in <paramref name="deviceId"/>, or null if it carries no
    /// VID/PID pair — which is the normal answer for a COM port that is not USB-attached (a
    /// motherboard serial port's ID starts <c>ACPI\</c>, a Bluetooth SPP port's <c>BTHENUM\</c>).
    /// </summary>
    internal static UsbPortDescriptor? ParseVidPid(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var match = VidPidRegex.Match(deviceId);
        if (!match.Success)
        {
            return null;
        }

        var vid = int.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var pid = int.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new UsbPortDescriptor(vid, pid);
    }

    /// <summary>
    /// Returns the descriptor of the first ID in <paramref name="deviceIds"/> that carries a
    /// VID/PID pair, or null if none does. Skipping over IDs without one is what lets a port
    /// claimed by both a USB entity and a non-USB one still be classified.
    /// </summary>
    internal static UsbPortDescriptor? SelectUsbDescriptor(IReadOnlyList<string> deviceIds)
    {
        for (var i = 0; i < deviceIds.Count; i++)
        {
            var descriptor = ParseVidPid(deviceIds[i]);
            if (descriptor != null)
            {
                return descriptor;
            }
        }

        return null;
    }
}
