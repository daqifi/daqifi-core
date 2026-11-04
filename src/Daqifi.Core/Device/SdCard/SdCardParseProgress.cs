namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Progress information for SD card file parsing.
/// </summary>
/// <param name="BytesRead">Total bytes read from the stream so far.</param>
/// <param name="TotalBytes">Total bytes in the stream, or -1 if unknown.</param>
/// <param name="MessagesRead">Total protobuf messages parsed so far.</param>
public sealed record SdCardParseProgress(long BytesRead, long TotalBytes, int MessagesRead)
{
    /// <summary>
    /// Gets how many of the data rows counted by <see cref="MessagesRead"/> the parser could
    /// not read and discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row the parser cannot read is skipped so that the rest of the file still imports, and
    /// this count is what says it happened: readings a caller never receives, and until this
    /// existed there was no way to know they were missing (closes #884). A caller reporting
    /// "every reading imported" should check this first.
    /// </para>
    /// <para>
    /// Reported by the CSV path. The final progress report of an enumeration carries the total
    /// for the whole file.
    /// </para>
    /// </remarks>
    public int RowsSkipped { get; init; }
}
