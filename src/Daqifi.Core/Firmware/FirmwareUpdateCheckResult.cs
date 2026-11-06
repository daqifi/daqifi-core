namespace Daqifi.Core.Firmware;

/// <summary>
/// Result of comparing a device's firmware version against the latest available release.
/// </summary>
public sealed class FirmwareUpdateCheckResult
{
    /// <summary>
    /// Whether an update is available (latest version is newer than the device version).
    /// </summary>
    /// <remarks>
    /// This is <c>false</c> whenever the device's version could not be read. Read
    /// <see cref="Status"/> to tell that case apart from a device that is already up to date.
    /// </remarks>
    public required bool UpdateAvailable { get; init; }

    /// <summary>
    /// The device's current firmware version, if it could be parsed.
    /// </summary>
    public FirmwareVersion? DeviceVersion { get; init; }

    /// <summary>
    /// The latest available release information.
    /// </summary>
    public FirmwareReleaseInfo? LatestRelease { get; init; }

    /// <summary>
    /// Why an update is, or is not, offered.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="DeviceVersion"/> and <see cref="LatestRelease"/> rather than
    /// stored, so it can never disagree with them. An unreadable device version wins over a
    /// missing release, because it is true whatever the release lookup returned, and it is the
    /// state a caller must never read as "an update is available".
    /// </remarks>
    public FirmwareUpdateStatus Status
    {
        get
        {
            if (DeviceVersion is not { } device) return FirmwareUpdateStatus.DeviceVersionUnknown;
            if (LatestRelease is not { } latest) return FirmwareUpdateStatus.NoReleaseAvailable;

            var cmp = latest.Version.CompareTo(device);
            if (cmp > 0) return FirmwareUpdateStatus.UpdateAvailable;
            if (cmp < 0) return FirmwareUpdateStatus.DeviceNewerThanLatest;
            return FirmwareUpdateStatus.UpToDate;
        }
    }
}
