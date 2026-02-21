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
    Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a WiFi module update using an external flashing tool.
    /// </summary>
    /// <param name="device">The connected streaming device to update.</param>
    /// <param name="firmwarePath">Path to a WiFi tool executable/script or directory containing it.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateWifiModuleAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
