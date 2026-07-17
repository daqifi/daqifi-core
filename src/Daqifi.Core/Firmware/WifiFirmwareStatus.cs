namespace Daqifi.Core.Firmware;

/// <summary>
/// Result of <see cref="IFirmwareUpdateService.CheckWifiFirmwareStatusAsync"/>:
/// the inputs Core uses to decide whether a WiFi update is needed plus the
/// boolean conclusion. Returned without mutating service state, so callers can
/// inspect, log, retry, or surface UI before deciding to call
/// <see cref="IFirmwareUpdateService.UpdateWifiModuleAsync"/>.
/// </summary>
/// <remarks>
/// When <see cref="Reason"/> is <see cref="WifiFirmwareStatusReason.UpToDate"/>
/// or <see cref="WifiFirmwareStatusReason.UpdateAvailable"/>, both
/// <see cref="CurrentChipInfo"/> and <see cref="LatestRelease"/> are non-null.
/// Other reasons leave one or both null and conservatively report
/// <see cref="IsUpToDate"/> = false so callers default to running the update.
/// </remarks>
public sealed record WifiFirmwareStatus
{
    /// <summary>
    /// The current WiFi chip info read from the device, or null if the device
    /// did not expose <see cref="ILanChipInfoProvider"/> or the query failed.
    /// </summary>
    public LanChipInfo? CurrentChipInfo { get; init; }

    /// <summary>
    /// The latest WiFi firmware release on GitHub, or null if the lookup
    /// failed (e.g. offline, rate-limited).
    /// </summary>
    public FirmwareReleaseInfo? LatestRelease { get; init; }

    /// <summary>
    /// True only when both versions are available AND the device version is
    /// at least the latest release. Any unknown is reported as false so the
    /// caller defaults to "needs update".
    /// </summary>
    public required bool IsUpToDate { get; init; }

    /// <summary>
    /// Why <see cref="IsUpToDate"/> has its current value — lets callers
    /// distinguish "definitively up to date" from "couldn't check, assuming not".
    /// </summary>
    public required WifiFirmwareStatusReason Reason { get; init; }
}

/// <summary>
/// Categorical outcome for <see cref="WifiFirmwareStatus"/>.
/// </summary>
public enum WifiFirmwareStatusReason
{
    /// <summary>Device version >= latest release version.</summary>
    UpToDate,

    /// <summary>Device version &lt; latest release version.</summary>
    UpdateAvailable,

    /// <summary>The device does not implement <see cref="ILanChipInfoProvider"/>.</summary>
    DeviceDoesNotSupportLanQuery,

    /// <summary>Querying the device for chip info failed.</summary>
    ChipInfoUnavailable,

    /// <summary>
    /// The WiFi module's saved settings report enabled (<c>LAN:ENAbled? = 1</c>) but
    /// its state machine was still not initialized (SCPI <c>-200</c>) even after a
    /// single <c>LAN:APPLY</c> kick and exhausting the retry budget. Distinct from
    /// <see cref="ChipInfoUnavailable"/> so callers can tell "known not-yet-ready
    /// state, already nudged" apart from a genuinely unresponsive device.
    /// </summary>
    LanNotInitialized,

    /// <summary>Looking up the latest release on GitHub failed.</summary>
    LatestReleaseUnavailable,

    /// <summary>Either version string failed to parse.</summary>
    VersionUnparseable,
}
