namespace Daqifi.Core.Firmware;

/// <summary>
/// Firmware update lifecycle states emitted by <see cref="IFirmwareUpdateService"/>.
/// </summary>
public enum FirmwareUpdateState
{
    /// <summary>
    /// No update operation is currently running.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// Preparing the device and transport for update.
    /// </summary>
    PreparingDevice = 1,

    /// <summary>
    /// Waiting for the device to enumerate as a HID bootloader.
    /// </summary>
    WaitingForBootloader = 2,

    /// <summary>
    /// Connecting to bootloader transport.
    /// </summary>
    Connecting = 3,

    /// <summary>
    /// Issuing flash erase commands.
    /// </summary>
    ErasingFlash = 4,

    /// <summary>
    /// Programming firmware bytes.
    /// </summary>
    Programming = 5,

    /// <summary>
    /// Verifying successful update and reconnect behavior.
    /// </summary>
    Verifying = 6,

    /// <summary>
    /// Jumping from bootloader to application firmware.
    /// </summary>
    JumpingToApp = 7,

    /// <summary>
    /// Update finished successfully.
    /// </summary>
    Complete = 8,

    /// <summary>
    /// Update terminated with an error or cancellation.
    /// </summary>
    Failed = 9,

    /// <summary>
    /// Recovering from a failure by re-erasing the application flash so the
    /// device is left in a clean bootloader state rather than a half-flashed
    /// one. Entered only on failures during <see cref="ErasingFlash"/>,
    /// <see cref="Programming"/>, or <see cref="Verifying"/>, where the HID
    /// bootloader is still connected and the flash may have been modified.
    /// </summary>
    CleaningUp = 10,

    /// <summary>
    /// The update failed, but cleanup succeeded: the application flash was
    /// re-erased and the device is in a clean bootloader state, ready to be
    /// re-flashed. This is a terminal failure state — the firmware was not
    /// installed — but the device is safe and recoverable by re-running the
    /// update.
    /// </summary>
    Recovered = 11
}
