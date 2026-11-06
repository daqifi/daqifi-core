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

    /// <summary>
    /// Gets a value indicating whether the file's last data row did not match the header's column
    /// count, and was therefore dropped as the truncated tail of a recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A log that is stopped rather than closed can end part-way through a row: the device writes
    /// rows as it goes, so a stop command, a power loss, or a card pulled out leaves whatever had
    /// been written. That row used to pass as a sample with fewer values, and the export wrote it
    /// as a row with empty cells — which a reader that treats an empty cell as zero sees as every
    /// remaining channel dropping to zero for one sample (issue #905).
    /// </para>
    /// <para>
    /// A truncated tail is normal, not exceptional, so the row is dropped rather than thrown
    /// over. This is what tells a clean end from a cut one, which is the same distinction the
    /// live path draws between a recording that ran its duration and one that was cancelled.
    /// </para>
    /// <para>
    /// Only the last row. A short row in the middle of a file is a parse problem rather than a
    /// truncated tail, and is counted in <see cref="RowsSkipped"/> with the other rows the parser
    /// could not read. Reported by the CSV path; the final progress report of an enumeration
    /// carries the answer for the whole file.
    /// </para>
    /// </remarks>
    public bool TruncatedFinalRow { get; init; }
}
