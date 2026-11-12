namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// What became of a request to turn CRC-protected SD writes on or off
/// (<see cref="ISdCardOperations.SetSdCardCrcProtectionAsync"/>).
/// </summary>
public enum SdCardCrcProtectionResult
{
    /// <summary>
    /// The device has applied the setting: every write the card takes from now on uses it.
    /// </summary>
    Applied = 0,

    /// <summary>
    /// The device stored the setting but has not applied it yet, because its SD task is not
    /// running: the device is in standby, or the shared SPI bus is handed to WiFi streaming. It
    /// takes effect the next time the SD task runs.
    /// </summary>
    /// <remarks>
    /// Until then the device will not mount the card. Starting an SD log, or any SD file
    /// operation, waits up to two seconds for the change to apply and then fails with SCPI
    /// <c>-200</c>, and the storage-space query is refused the same way. Power the device up or
    /// stop the WiFi stream, then call
    /// <see cref="ISdCardOperations.SetSdCardCrcProtectionAsync"/> again with the same value: it
    /// returns <see cref="Applied"/> once the change has landed.
    /// </remarks>
    Pending = 1
}
