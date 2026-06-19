namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Free and total byte counts reported by the device's SD card via the
/// <c>SYSTem:STORage:SD:SPACe?</c> SCPI query.
/// </summary>
/// <param name="FreeBytes">Free space remaining on the SD card, in bytes.</param>
/// <param name="TotalBytes">Total capacity of the SD card, in bytes.</param>
public sealed record SdCardStorageInfo(long FreeBytes, long TotalBytes)
{
    /// <summary>
    /// Used space on the SD card, in bytes. Computed as <c>TotalBytes - FreeBytes</c>.
    /// </summary>
    public long UsedBytes => TotalBytes - FreeBytes;
}
