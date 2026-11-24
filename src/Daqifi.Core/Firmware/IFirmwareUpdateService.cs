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
    /// <param name="target">
    /// The board to update, for when several bootloaders are present. Null names no board.
    /// </param>
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
    /// <para>
    /// When <paramref name="target"/> names no board and more than one HID bootloader device is
    /// present, the board to update is ambiguous and the update stops with an
    /// <see cref="AmbiguousBootloaderTargetException"/> before any flash is erased (#837).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="hexFilePath"/> is empty, or <paramref name="target"/> holds an identifier that
    /// is empty or whitespace.
    /// </exception>
    Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        FirmwareUpdateTarget? target = null,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a WiFi module update using an external flashing tool.
    /// </summary>
    /// <param name="device">The connected streaming device to update.</param>
    /// <param name="firmwarePath">Path to a WiFi tool executable/script or directory containing it.</param>
    /// <param name="options">Options for the update. Null uses the defaults.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateWifiModuleAsync(
        IStreamingDevice device,
        string firmwarePath,
        WifiUpdateOptions? options = null,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the device for its current WiFi chip info and looks up the
    /// latest WiFi firmware release, returning the comparison result without
    /// mutating service state. Lets callers (typically GUI/desktop integrations)
    /// surface their own logging, retry policy, or UI before deciding whether
    /// to call <see cref="UpdateWifiModuleAsync"/>. Set
    /// <see cref="WifiUpdateOptions.SkipVersionCheck"/> on that call to avoid a second probe.
    /// </summary>
    /// <param name="device">The connected streaming device to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken = default);
}
