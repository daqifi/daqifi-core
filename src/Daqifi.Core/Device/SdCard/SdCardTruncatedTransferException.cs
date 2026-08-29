using System;
using System.Globalization;

#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card file download ends at the <c>__END_OF_FILE__</c> marker having
/// received fewer bytes than the directory listing reports for the file. The transfer looks
/// complete on the wire — the marker is the only completion signal the firmware sends — but
/// the content is short, so what reached the destination is a partial file or not the file
/// at all.
/// </summary>
/// <remarks>
/// The observed shape on real hardware is a device that answers <c>SD:GET</c> with a SCPI
/// error line and then the end-of-file marker: a 34-byte "download" of the text
/// <c>**ERROR: -200, "Execution error"</c> standing in for a multi-kilobyte log. Nothing in
/// the transfer itself distinguishes that from file content, and the device's error queue
/// reads clean afterwards, so the listed size is the only evidence available — which is why
/// this is raised on the size comparison rather than by inspecting the payload.
/// <para>
/// Whatever was received has already been written to the caller's destination stream by the
/// time this is thrown. It is not a usable file and must be discarded; the download is not
/// retried automatically, because a retry would append to that same stream rather than
/// replace it.
/// </para>
/// <para>
/// A transfer that is <em>longer</em> than the listed size does not raise this. A listing can
/// legitimately be stale — a file being appended to by an active logging session grows after
/// it was listed — and in that case the extra bytes are real content, not evidence of a
/// failure.
/// </para>
/// </remarks>
public class SdCardTruncatedTransferException : SdCardOperationException
{
    /// <summary>
    /// Gets the name of the file whose transfer came up short.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the size the directory listing reported for the file. Always greater than
    /// <see cref="BytesReceived"/>.
    /// </summary>
    public long ListedSizeInBytes { get; }

    /// <summary>
    /// Gets the number of file bytes actually received before the end-of-file marker. Always
    /// greater than zero: a transfer with no content bytes at all is reported as
    /// <see cref="SdCardEmptyTransferException"/> instead.
    /// </summary>
    public long BytesReceived { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardTruncatedTransferException"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file whose transfer came up short.</param>
    /// <param name="listedSizeInBytes">The size the directory listing reported for the file.</param>
    /// <param name="bytesReceived">The number of file bytes actually received.</param>
    public SdCardTruncatedTransferException(
        string fileName,
        long listedSizeInBytes,
        long bytesReceived)
        : base(
            BuildMessage(fileName, listedSizeInBytes, bytesReceived),
            Array.Empty<string>())
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ListedSizeInBytes = listedSizeInBytes;
        BytesReceived = bytesReceived;
    }

    private static string BuildMessage(string fileName, long listedSizeInBytes, long bytesReceived)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "SD card file '{0}' ended at the end-of-file marker after only {1} of the {2} bytes " +
            "the directory listing reports, so the download is incomplete. The device served a " +
            "short reply — commonly a SCPI error line in place of the file — and what was " +
            "written is not the file; discard it and retry the download.",
            fileName,
            bytesReceived,
            listedSizeInBytes);
    }
}
