namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Progress information reported during an SD card file download.
/// </summary>
public sealed record SdCardTransferProgress
{
    /// <summary>
    /// Total bytes received from the device so far.
    /// </summary>
    public required long BytesReceived { get; init; }

    /// <summary>
    /// Gets the name of the file being downloaded.
    /// </summary>
    public required string FileName { get; init; }
}
