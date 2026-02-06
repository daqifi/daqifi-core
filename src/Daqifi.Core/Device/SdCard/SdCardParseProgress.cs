namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Progress information for SD card file parsing.
/// </summary>
/// <param name="BytesRead">Total bytes read from the stream so far.</param>
/// <param name="TotalBytes">Total bytes in the stream, or -1 if unknown.</param>
/// <param name="MessagesRead">Total protobuf messages parsed so far.</param>
public sealed record SdCardParseProgress(long BytesRead, long TotalBytes, int MessagesRead);
