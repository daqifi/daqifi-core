namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Free and total byte counts reported by the device's SD card via the
/// <c>SYSTem:STORage:SD:SPACe?</c> SCPI query.
/// </summary>
public sealed record SdCardStorageInfo
{
    /// <summary>
    /// Free space remaining on the SD card, in bytes.
    /// </summary>
    public required long FreeBytes { get; init; }

    /// <summary>
    /// Total capacity of the SD card, in bytes.
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Used space on the SD card, in bytes. Computed as <c>TotalBytes - FreeBytes</c>.
    /// </summary>
    public long UsedBytes => TotalBytes - FreeBytes;
}
