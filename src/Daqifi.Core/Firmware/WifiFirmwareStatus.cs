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
    /// <remarks>
    /// This is a "newest available" answer and therefore depends on the GitHub
    /// lookup succeeding. For the network-independent "is this module supported
    /// at all" question — the one a manufacturing or field check actually asks —
    /// use <see cref="MeetsMinimumSupportedVersion"/>.
    /// </remarks>
    public required bool IsUpToDate { get; init; }

    /// <summary>
    /// The minimum WINC firmware version Core considers supported
    /// (<see cref="FirmwareUpdateServiceOptions.MinimumSupportedWifiFirmwareVersion"/>),
    /// or null if that option could not be parsed. Reported on every result — including
    /// the ones where the device could not be read — so a caller can always state the bar
    /// it was judging against.
    /// </summary>
    public FirmwareVersion? MinimumSupportedVersion { get; init; }

    /// <summary>
    /// Whether the device's reported WiFi firmware is at least
    /// <see cref="MinimumSupportedVersion"/>, or null when that could not be determined
    /// (no chip info was read, the device version did not parse, or the configured
    /// minimum did not parse).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="IsUpToDate"/> this needs no network access: it compares the
    /// device's own reported version against a firmware-contract constant Core owns. That
    /// makes it the right signal for a manufacturing check ("device &gt;= minimum") and the
    /// reason an offline or rate-limited GitHub lookup no longer forces a reflash of a
    /// module that is demonstrably supported.
    ///
    /// Deliberately a three-state <see cref="bool"/>?: <c>false</c> means "read the device
    /// and it is below the minimum", while <c>null</c> means "could not tell". Collapsing
    /// those two into one <c>false</c> is exactly the conflation that made an unreadable
    /// device indistinguishable from an outdated one.
    /// </remarks>
    public bool? MeetsMinimumSupportedVersion { get; init; }

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
    /// its state machine was still not initialized (SCPI <c>-200</c>) after exhausting
    /// the retry budget. When <see cref="FirmwareUpdateServiceOptions.KickLanApplyOnNotInitialized"/>
    /// is enabled and the device was connected, Core also attempted a one-shot
    /// <c>LAN:APPLY</c> kick before giving up — but this reason is reported either way,
    /// so it does not by itself confirm a kick was sent. Distinct from
    /// <see cref="ChipInfoUnavailable"/> so callers can tell "known not-yet-ready
    /// state" apart from a genuinely unresponsive device.
    /// </summary>
    LanNotInitialized,

    /// <summary>Looking up the latest release on GitHub failed.</summary>
    LatestReleaseUnavailable,

    /// <summary>Either version string failed to parse.</summary>
    VersionUnparseable,
}
