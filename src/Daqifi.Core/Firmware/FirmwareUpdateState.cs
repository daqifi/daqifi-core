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
    /// Verifying that the flashed image is correct — the PIC32 flash CRC is read back and
    /// compared against the firmware image. A failure here means the firmware is NOT correctly
    /// installed. Post-flash reconnect is <see cref="ReconnectingAfterFlash"/>, a separate state
    /// with the opposite severity.
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
    Recovered = 11,

    /// <summary>
    /// Re-establishing the serial transport after a fully flashed and verified WiFi (WINC)
    /// module image, and restoring the device's LAN configuration.
    /// </summary>
    /// <remarks>
    /// A failure in this state is benign and environmental: the firmware flashed and verified
    /// successfully, and only the host's re-enumeration of the serial port timed out. It is
    /// deliberately distinct from <see cref="Verifying"/>, which is a genuine flash failure —
    /// the two used to share <see cref="Verifying"/>, leaving a consumer to discriminate on a
    /// human-readable progress string and users of a successful WiFi flash being told their
    /// flash CRC mismatched (#398 gap 4).
    /// </remarks>
    ReconnectingAfterFlash = 12
}
