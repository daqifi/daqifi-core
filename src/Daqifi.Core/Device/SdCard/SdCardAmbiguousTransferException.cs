using System;
using System.Globalization;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card file download reaches the <c>__END_OF_FILE__</c> marker, more file
/// content follows it, and no directory listing size is available to say how long the file is.
/// The marker is ASCII text, so a log file can hold it; when that happens the marker no longer
/// marks the end of anything, and without a listed size there is nothing left to locate the end
/// of the file with.
/// </summary>
/// <remarks>
/// The firmware sends nothing after the marker that would confirm the transfer is over
/// (daqifi/daqifi-nyquist-firmware#1180), so the marker is the only completion signal there is.
/// The honest answer for a file that contains it is to report the ambiguity: the alternative is
/// to return the bytes before the marker as a finished download, which is a silently truncated
/// file and leaves the rest of it on the wire for the next command to read (#852).
/// <para>
/// The cure is the listed size. With it the receiver copies exactly that many bytes and reads
/// the marker text inside them as the file content it is, so fetching a directory listing before
/// the download — which <see cref="SdCardOperations"/> does — makes this file downloadable.
/// </para>
/// <para>
/// The bytes before the marker have already been written to the caller's destination stream by
/// the time this is thrown. They are genuine file content but they are not the whole file, so
/// discard them.
/// </para>
/// </remarks>
public class SdCardAmbiguousTransferException : SdCardOperationException
{
    /// <summary>
    /// Gets the name of the file whose end could not be located.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the number of file bytes that arrived before the first <c>__END_OF_FILE__</c> marker.
    /// That is how much of the file reached the destination stream, and it is less than the whole
    /// file.
    /// </summary>
    public long BytesReceived { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardAmbiguousTransferException"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file whose end could not be located.</param>
    /// <param name="bytesReceived">
    /// The number of file bytes received before the first end-of-file marker.
    /// </param>
    public SdCardAmbiguousTransferException(string fileName, long bytesReceived)
        : base(
            BuildMessage(fileName, bytesReceived),
            Array.Empty<string>())
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        BytesReceived = bytesReceived;
    }

    private static string BuildMessage(string fileName, long bytesReceived)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "SD card file '{0}' contains the text __END_OF_FILE__, which is also the marker the " +
            "device sends to end a transfer, and more file content follows it. Without the size " +
            "from a directory listing the end of the file cannot be found, so the {1} bytes " +
            "before the marker are not the whole file; discard them and download the file again " +
            "with a directory listing available.",
            fileName,
            bytesReceived);
    }
}
