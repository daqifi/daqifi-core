using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Receives raw bytes from a transport stream during an SD card file download.
/// Accumulates data and writes it to a destination stream, detecting the
/// <c>__END_OF_FILE__</c> marker that signals transfer completion.
/// </summary>
public sealed class SdCardFileReceiver
{
    /// <summary>
    /// The ASCII marker appended by the firmware after all file data has been sent.
    /// </summary>
    internal static readonly byte[] EndOfFileMarker =
        Encoding.ASCII.GetBytes("__END_OF_FILE__");

    private readonly Stream _sourceStream;
    private readonly int _bufferSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileReceiver"/> class.
    /// </summary>
    /// <param name="sourceStream">The transport stream to read raw bytes from.</param>
    /// <param name="bufferSize">Read buffer size in bytes. Defaults to 16384 (16 KB).</param>
    public SdCardFileReceiver(Stream sourceStream, int bufferSize = 16384)
    {
        _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be greater than zero.");
        }

        _bufferSize = bufferSize;
    }

    /// <summary>
    /// Reads bytes from the source stream until the <c>__END_OF_FILE__</c> marker is detected,
    /// writing all file content (minus the marker) to the destination stream.
    /// </summary>
    /// <param name="destinationStream">The stream to write file data to.</param>
    /// <param name="fileName">The file name, used for progress reporting.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="timeout">
    /// Maximum time to wait for the complete file transfer.
    /// If the timeout elapses before the EOF marker is received, a
    /// <see cref="SdCardTransferStalledException"/> is thrown.
    /// </param>
    /// <param name="listedFileSizeBytes">
    /// The size the device's directory listing reported for this file, when known. It is the only
    /// thing that separates a wedged SD subsystem from a genuinely 0-byte file, so a marker-only
    /// transfer raises <see cref="SdCardEmptyTransferException"/> only when this says the file is
    /// non-empty. Pass <c>0</c> for a listed empty file to have it return 0 bytes as a legitimate
    /// empty download. When <c>null</c> (no listing available) the conservative behavior is kept
    /// and a marker-only transfer throws.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of file bytes written (excluding the EOF marker).</returns>
    /// <exception cref="SdCardTransferStalledException">
    /// Thrown when the transfer stops making progress before the EOF marker arrives — the
    /// transport went quiet, closed, or the timeout elapsed. Inspect
    /// <see cref="SdCardTransferStalledException.Reason"/> to tell those apart.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    /// <exception cref="SdCardEmptyTransferException">
    /// Thrown when the EOF marker arrives with zero preceding file bytes for a file that
    /// <paramref name="listedFileSizeBytes"/> reports as non-empty (or whose listed size is
    /// unknown), meaning the device opened the file but sent no content before closing it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="listedFileSizeBytes"/> is negative.
    /// </exception>
    public async Task<long> ReceiveAsync(
        Stream destinationStream,
        string fileName,
        IProgress<SdCardTransferProgress>? progress = null,
        TimeSpan? timeout = null,
        long? listedFileSizeBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);
        ArgumentNullException.ThrowIfNull(fileName);

        if (listedFileSizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(listedFileSizeBytes),
                listedFileSizeBytes,
                "Listed file size cannot be negative.");
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        var buffer = new byte[_bufferSize];
        long totalBytesReceived = 0;

        // We keep a trailing window of the last N bytes to detect the EOF marker
        // even when it's split across chunk boundaries.
        var trailingBytes = new byte[EndOfFileMarker.Length];
        var trailingCount = 0;

        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _sourceStream.ReadAsync(buffer, 0, buffer.Length, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new SdCardTransferStalledException(
                        fileName,
                        totalBytesReceived,
                        SdCardTransferStallReason.TransferTimeout,
                        effectiveTimeout,
                        ex);
                }

                if (bytesRead == 0)
                {
                    // A zero-byte read is NOT proof the stream closed. Over USB serial — the
                    // transport SD download runs on — Core sets a per-read SerialPort.ReadTimeout
                    // and .NET's SerialStream.ReadAsync returns 0 on that timeout instead of
                    // throwing, so zero bytes is the ORDINARY stall signal there. Only an
                    // unreadable stream is genuinely closed; report the two distinctly so a
                    // caller can retry a stall and give up on a closed transport (#398 gap 1).
                    var reason = _sourceStream.CanRead
                        ? SdCardTransferStallReason.NoDataReceived
                        : SdCardTransferStallReason.TransportClosed;

                    throw new SdCardTransferStalledException(fileName, totalBytesReceived, reason);
                }

                // Check if the EOF marker is contained in or spans the new data
                var (foundEof, eofPosition) = FindEndOfFileMarker(
                    trailingBytes, trailingCount, buffer, bytesRead);

                if (foundEof)
                {
                    // Write only the data bytes before the marker
                    // eofPosition is relative to the combined [trailing + new] buffer.
                    // The data we haven't written yet from trailing is trailingCount bytes,
                    // so we need to figure out how much of the new data is actual file content.
                    var newDataFileBytes = eofPosition - trailingCount;

                    if (newDataFileBytes < 0)
                    {
                        // The marker started within the trailing bytes. We only need to
                        // write the portion of the trailing buffer before the marker.
                        var trailingToWrite = eofPosition;
                        if (trailingToWrite > 0)
                        {
                            await destinationStream.WriteAsync(trailingBytes, 0, trailingToWrite, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += trailingToWrite;
                        }
                    }
                    else
                    {
                        // Write deferred trailing bytes
                        if (trailingCount > 0)
                        {
                            await destinationStream.WriteAsync(trailingBytes, 0, trailingCount, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += trailingCount;
                        }

                        // Write the portion of new data before the marker
                        if (newDataFileBytes > 0)
                        {
                            await destinationStream.WriteAsync(buffer, 0, newDataFileBytes, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += newDataFileBytes;
                        }
                    }

                    if (totalBytesReceived == 0 && listedFileSizeBytes != 0)
                    {
                        // An immediate EOF marker with no preceding file bytes is ambiguous on the
                        // wire: a transient/wedged SD subsystem opens the file and sends nothing
                        // (#264), and so does a genuinely 0-byte file left behind by an interrupted
                        // logging session. The listing's reported size is the only discriminator,
                        // so honor it — a listed 0-byte file falls through and returns 0 bytes as a
                        // legitimate empty download. With no listed size we keep the conservative
                        // #264 behavior so a wedged subsystem is still caught (#398 gap 2).
                        throw new SdCardEmptyTransferException(fileName, listedFileSizeBytes);
                    }

                    progress?.Report(new SdCardTransferProgress(totalBytesReceived, fileName));
                    return totalBytesReceived;
                }

                // No EOF marker found — combine trailing + new data, write
                // everything except the last markerLength bytes (the new trailing window).
                var combinedLen = trailingCount + bytesRead;

                if (combinedLen >= EndOfFileMarker.Length)
                {
                    // How many bytes can we safely write (not part of potential marker)?
                    var safeCount = combinedLen - EndOfFileMarker.Length;

                    if (safeCount > 0)
                    {
                        // Write from the trailing portion first
                        var fromTrailing = Math.Min(safeCount, trailingCount);
                        if (fromTrailing > 0)
                        {
                            await destinationStream.WriteAsync(trailingBytes, 0, fromTrailing, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += fromTrailing;
                        }

                        // Write from the new data
                        var fromNew = safeCount - fromTrailing;
                        if (fromNew > 0)
                        {
                            await destinationStream.WriteAsync(buffer, 0, fromNew, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += fromNew;
                        }
                    }

                    // Build the new trailing window from the last markerLength bytes of [trailing + new]
                    var newTrailingCount = EndOfFileMarker.Length;
                    var fromNewData = Math.Min(bytesRead, newTrailingCount);
                    var fromTrailingData = newTrailingCount - fromNewData;

                    if (fromTrailingData > 0)
                    {
                        Array.Copy(trailingBytes, trailingCount - fromTrailingData, trailingBytes, 0, fromTrailingData);
                    }

                    if (fromNewData > 0)
                    {
                        Array.Copy(buffer, bytesRead - fromNewData, trailingBytes, fromTrailingData, fromNewData);
                    }

                    trailingCount = newTrailingCount;
                }
                else
                {
                    // Combined data is still shorter than the marker — just accumulate
                    Array.Copy(buffer, 0, trailingBytes, trailingCount, bytesRead);
                    trailingCount = combinedLen;
                }

                progress?.Report(new SdCardTransferProgress(totalBytesReceived, fileName));
            }
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SdCardTransferStalledException(
                fileName,
                totalBytesReceived,
                SdCardTransferStallReason.TransferTimeout,
                effectiveTimeout,
                ex);
        }
    }

    /// <summary>
    /// Searches for the EOF marker in the combined view of trailing bytes and new data.
    /// The marker could span the boundary between the two buffers.
    /// </summary>
    /// <returns>
    /// A tuple of (found, position) where position is the start index of the marker
    /// in the virtual combined buffer [trailing..new].
    /// </returns>
    internal static (bool Found, int Position) FindEndOfFileMarker(
        byte[] trailing, int trailingCount, byte[] newData, int newDataLength)
    {
        if (trailingCount + newDataLength < EndOfFileMarker.Length)
        {
            return (false, -1);
        }

        // Build a search window that covers the overlap zone.
        // The marker can start at any position from (trailingCount - markerLength + 1)
        // to (trailingCount + newDataLength - markerLength).
        // But we only need to scan positions that include new data to avoid re-scanning.
        var markerLen = EndOfFileMarker.Length;
        var searchStart = Math.Max(0, trailingCount - markerLen + 1);
        var searchEnd = trailingCount + newDataLength - markerLen;

        for (var pos = searchStart; pos <= searchEnd; pos++)
        {
            var match = true;
            for (var j = 0; j < markerLen; j++)
            {
                var idx = pos + j;
                byte b;
                if (idx < trailingCount)
                {
                    b = trailing[idx];
                }
                else
                {
                    b = newData[idx - trailingCount];
                }

                if (b != EndOfFileMarker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return (true, pos);
            }
        }

        return (false, -1);
    }
}
