using Daqifi.Core.Device;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Orchestrates firmware update flows for DAQiFi devices.
/// </summary>
public interface IFirmwareUpdateService
{
    /// <summary>
    /// Gets the current update state.
    /// </summary>
    FirmwareUpdateState CurrentState { get; }

    /// <summary>
    /// Raised whenever the update state transitions.
    /// Handlers should not synchronously invoke update operations on this same service instance.
    /// </summary>
    event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Executes a PIC32 firmware update through bootloader mode using a local HEX file.
    /// </summary>
    /// <param name="device">The connected streaming device to update.</param>
    /// <param name="hexFilePath">Path to an Intel HEX firmware file.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// On failure this throws <see cref="FirmwareUpdateException"/>. If the failure
    /// occurs after flash has been written (while the HID bootloader is still
    /// connected), the service automatically re-erases the application flash so the
    /// device is left in a clean, re-flashable bootloader state rather than a
    /// half-flashed one. In that case <see cref="CurrentState"/> ends at
    /// <see cref="FirmwareUpdateState.Recovered"/> (preceded by a
    /// <see cref="FirmwareUpdateState.CleaningUp"/> transition) and the exception's
    /// recovery guidance reflects that the device is safe to re-flash; if the
    /// re-erase itself cannot complete, <see cref="CurrentState"/> ends at
    /// <see cref="FirmwareUpdateState.Failed"/>. The exception's
    /// <see cref="FirmwareUpdateException.FailedState"/> always reports where the
    /// original failure occurred, regardless of cleanup outcome.
    /// </remarks>
    Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a PIC32 firmware update targeting one specific bootloader by device path, for when
    /// several identical bootloaders (same VID/PID, and no serial to tell them apart) are present.
    /// Added as a default interface method so existing implementers and callers of the untargeted
    /// overload above are unaffected; implementations that support targeting (the production
    /// <c>FirmwareUpdateService</c>) override it. The default throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="device">The connected streaming device to update.</param>
    /// <param name="hexFilePath">Path to an Intel HEX firmware file.</param>
    /// <param name="progress">Progress reporter (may be null).</param>
    /// <param name="targetDevicePath">
    /// HID device path identifying which bootloader to flash (from discovery's
    /// <c>HidDeviceInfo.DevicePath</c>). Null behaves like the untargeted overload.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress,
        string? targetDevicePath,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This IFirmwareUpdateService implementation does not support targeted (device-path) firmware updates.");

    /// <summary>
    /// Executes a WiFi module update using an external flashing tool.
    /// </summary>
    /// <param name="device">The connected streaming device to update.</param>
    /// <param name="firmwarePath">Path to a WiFi tool executable/script or directory containing it.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="skipVersionCheck">
    /// When true, bypass the internal version probe and always run the flash
    /// flow. Use after a separate <see cref="CheckWifiFirmwareStatusAsync"/>
    /// call so the device isn't queried twice — see issue #143 for the
    /// motivating callsite.
    /// </param>
    // skipVersionCheck added AFTER cancellationToken (technically violates
    // CA1068 "CancellationToken should be last") to avoid breaking source
    // compat for any existing positional caller passing CancellationToken
    // as the 4th argument. Additivity wins over strict style here.
#pragma warning disable CA1068
    Task UpdateWifiModuleAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool skipVersionCheck = false);
#pragma warning restore CA1068

    /// <summary>
    /// Probes the device for its current WiFi chip info and looks up the
    /// latest WiFi firmware release, returning the comparison result without
    /// mutating service state. Lets callers (typically GUI/desktop integrations)
    /// surface their own logging, retry policy, or UI before deciding whether
    /// to call <see cref="UpdateWifiModuleAsync"/>. Pass
    /// <c>skipVersionCheck: true</c> to that call to avoid a second probe.
    /// </summary>
    /// <param name="device">The connected streaming device to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken = default);
}
