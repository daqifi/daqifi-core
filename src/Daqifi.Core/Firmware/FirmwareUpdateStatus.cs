namespace Daqifi.Core.Firmware;

/// <summary>
/// The outcome of a firmware update check. It names the reason an update is, or is not,
/// offered, which a plain <c>bool</c> cannot do.
/// </summary>
/// <remarks>
/// A flash is the one operation in this library that can leave a board unbootable, so
/// "I cannot read the installed version" must be its own answer. It used to be folded into
/// "an update is available", and the version then offered could be older than the firmware
/// already on the board.
/// </remarks>
public enum FirmwareUpdateStatus
{
    /// <summary>
    /// The device's reported version could not be read, so no update is offered. Tell the user
    /// the installed version is unknown. A caller that flashes anyway is making a deliberate
    /// choice.
    /// </summary>
    DeviceVersionUnknown = 0,

    /// <summary>
    /// No release was found to compare against, so no update is offered.
    /// </summary>
    NoReleaseAvailable = 1,

    /// <summary>
    /// The device already runs the newest release.
    /// </summary>
    UpToDate = 2,

    /// <summary>
    /// The newest release is newer than the device's firmware. This is the only status for
    /// which <see cref="FirmwareUpdateCheckResult.UpdateAvailable"/> is <c>true</c>.
    /// </summary>
    UpdateAvailable = 3,

    /// <summary>
    /// The device's firmware is newer than the newest release, which happens on a pre-release
    /// or a developer build. No update is offered, because installing the release would be a
    /// downgrade.
    /// </summary>
    DeviceNewerThanLatest = 4
}
