namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// What became of a request to turn CRC-protected SD writes on or off
/// (<see cref="ISdCardOperations.SetSdCardCrcProtectionAsync"/>).
/// </summary>
public enum SdCardCrcProtectionResult
{
    /// <summary>
    /// A card is in the slot, initialized in the requested mode, and mountable: Core mounted it
    /// and read its mode back to check. Every write it takes from now on uses that mode, and the
    /// next SD operation finds it ready.
    /// </summary>
    Applied = 0,

    /// <summary>
    /// The device stored the setting, but no card is ready with it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The causes, all of which leave the setting in place to apply later:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The device's SD task is not running: it is in standby, or WiFi streaming has the shared
    /// SPI bus. The change lands when the SD task next runs. Until then, starting an SD log or
    /// any SD file operation can wait up to two seconds and fail with SCPI <c>-200</c>, and the
    /// storage-space query can be refused the same way. Power the device up or stop the WiFi
    /// stream.
    /// </description></item>
    /// <item><description>
    /// There is no card in the slot. The next card inserted is initialized in the new mode.
    /// </description></item>
    /// <item><description>
    /// The card refuses CRC checking. With protection on, the device never mounts such a card
    /// rather than run it unprotected, so it cannot be logged to until protection is turned off
    /// again.
    /// </description></item>
    /// <item><description>
    /// The device cannot mount the card at all, for example an unformatted or damaged card.
    /// </description></item>
    /// </list>
    /// <para>
    /// Once the cause is dealt with, call <see cref="ISdCardOperations.SetSdCardCrcProtectionAsync"/>
    /// again with the same value: it returns <see cref="Applied"/> when a card is ready in the
    /// new mode.
    /// </para>
    /// </remarks>
    Pending = 1
}
