namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Progress information reported during an SD card file download.
/// </summary>
/// <param name="BytesReceived">Total bytes received from the device so far.</param>
/// <param name="FileName">The name of the file being downloaded.</param>
public sealed record SdCardTransferProgress(long BytesReceived, string FileName);
