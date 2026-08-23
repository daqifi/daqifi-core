namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Maps a device part number onto the discovery-facing <see cref="DeviceType"/>.
/// </summary>
/// <remarks>
/// Part-number recognition itself lives in one place only —
/// <see cref="DeviceTypeDetector.DetectFromPartNumber"/>. This type exists purely to
/// carry that result across to the separate <c>Discovery.DeviceType</c> enum that
/// <see cref="IDeviceInfo"/> exposes, so that every transport's finder shares a single
/// part-number table. Issue #283 was a Nyquist2 reported as <see cref="DeviceType.Unknown"/>
/// because per-transport copies of that table had drifted apart.
/// </remarks>
internal static class DiscoveryDeviceTypeMapper
{
    /// <summary>
    /// Detects the discovery <see cref="DeviceType"/> for a device part number.
    /// </summary>
    /// <param name="partNumber">The device part number (e.g. "Nq1"), or null.</param>
    /// <returns>The detected type, or <see cref="DeviceType.Unknown"/> if not recognized.</returns>
    internal static DeviceType FromPartNumber(string? partNumber)
        => FromCoreDeviceType(DeviceTypeDetector.DetectFromPartNumber(partNumber));

    /// <summary>
    /// Converts a <see cref="Device.DeviceType"/> to the matching discovery <see cref="DeviceType"/>.
    /// </summary>
    internal static DeviceType FromCoreDeviceType(Device.DeviceType deviceType)
    {
        return deviceType switch
        {
            Device.DeviceType.Nyquist1 => DeviceType.Nyquist1,
            Device.DeviceType.Nyquist2 => DeviceType.Nyquist2,
            Device.DeviceType.Nyquist3 => DeviceType.Nyquist3,
            _ => DeviceType.Unknown
        };
    }
}
