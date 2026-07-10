namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Parses the PnP device instance ID embedded in a Windows HID device interface path. Kept
/// separate from <see cref="WindowsUsbLocationProvider"/> (which is Windows-only because it
/// calls into <see cref="System.Management"/>) so this pure string-manipulation logic can be
/// unit tested on any platform without WMI/hardware access.
/// </summary>
internal static class HidDevicePathParser
{
    private const string DevicePathPrefix = @"\\?\";

    /// <summary>
    /// Extracts the PnP device instance ID embedded in a HID device interface path (e.g.
    /// <c>\\?\hid#vid_04d8&amp;pid_003c#7&amp;1a2b3c4d&amp;0&amp;0000#{4d1e55b2-f46c-11d0-894f-00a0c90c8b6e}</c>
    /// → <c>HID\VID_04D8&amp;PID_003C\7&amp;1A2B3C4D&amp;0&amp;0000</c>), or null if
    /// <paramref name="devicePath"/> doesn't match the expected shape. A device interface path
    /// encodes the instance ID with '#' in place of '\', followed by a trailing
    /// '#{device-interface-class-guid}' segment that must be stripped first.
    /// </summary>
    internal static string? ParseInstanceId(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var path = devicePath.StartsWith(DevicePathPrefix, StringComparison.OrdinalIgnoreCase)
            ? devicePath[DevicePathPrefix.Length..]
            : devicePath;

        var lastHash = path.LastIndexOf('#');
        if (lastHash < 0)
        {
            return null;
        }

        var guidSegment = path[(lastHash + 1)..];
        if (guidSegment.Length < 2 || guidSegment[0] != '{' || guidSegment[^1] != '}')
        {
            return null;
        }

        var instanceSegments = path[..lastHash];
        if (instanceSegments.Length == 0)
        {
            return null;
        }

        return instanceSegments.Replace('#', '\\').ToUpperInvariant();
    }
}
