using System;
using System.Globalization;

#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card file download ends at the <c>__TRANSFER_ERROR__</c> marker: the device
/// hit a read error part-way through serving the file and said so instead of finishing. The
/// transfer is over and the file is incomplete, but the bytes that arrived before the marker are
/// genuine file content.
/// </summary>
/// <remarks>
/// The marker arrived with firmware v3.7.3 (daqifi/daqifi-nyquist-firmware#725). Before it, a
/// read-side failure mid-transfer sent nothing at all and the host could only notice the silence,
/// so the same fault surfaced as <see cref="SdCardTransferStalledException"/> with
/// <see cref="SdCardTransferStallReason.NoDataReceived"/> — blaming the transport for what was
/// actually a card read error. This exception is what separates the two.
/// <para>
/// It does not replace the timeout. Firmware v3.7.3 still ends a peer-stall abort after partial
/// data with no terminator at all, by design, so a quiet transfer is still reported as a stall.
/// </para>
/// <para>
/// The <see cref="BytesReceived"/> bytes before the marker have already been written to the
/// caller's destination stream. Unlike <see cref="SdCardTruncatedTransferException"/>, they are
/// real file content rather than a short reply standing in for the file, so a caller that can use
/// a partial log may keep them; the download is not retried automatically, because a retry would
/// append to that same stream rather than replace it.
/// </para>
/// </remarks>
public class SdCardTransferErrorException : SdCardOperationException
{
    /// <summary>
    /// Gets the name of the file whose transfer the device abandoned.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the number of file bytes received before the transfer-error marker. These have
    /// already been written to the destination stream and are genuine file content.
    /// </summary>
    public long BytesReceived { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardTransferErrorException"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file whose transfer the device abandoned.</param>
    /// <param name="bytesReceived">The number of file bytes received before the marker.</param>
    public SdCardTransferErrorException(string fileName, long bytesReceived)
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
            "The device reported a read error while sending SD card file '{0}' and ended the " +
            "transfer after {1} bytes. Those bytes are genuine file content, but the file is " +
            "incomplete; retry the download, and if it keeps failing the card or that file may " +
            "be damaged.",
            fileName,
            bytesReceived);
    }
}
