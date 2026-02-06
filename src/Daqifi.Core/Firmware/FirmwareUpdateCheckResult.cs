namespace Daqifi.Core.Firmware;

/// <summary>
/// Result of comparing a device's firmware version against the latest available release.
/// </summary>
public sealed class FirmwareUpdateCheckResult
{
    /// <summary>
    /// Whether an update is available (latest version is newer than the device version).
    /// </summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>
    /// The device's current firmware version, if it could be parsed.
    /// </summary>
    public FirmwareVersion? DeviceVersion { get; init; }

    /// <summary>
    /// The latest available release information.
    /// </summary>
    public FirmwareReleaseInfo? LatestRelease { get; init; }
}
