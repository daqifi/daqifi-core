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

    /// <summary>
    /// How long the transfer may go without receiving a single byte before it is declared stalled,
    /// when the caller does not specify a window of its own.
    /// </summary>
    /// <remarks>
    /// Only a network transport actually needs this. On USB serial the port has a per-read
    /// <c>ReadTimeout</c> and <c>SerialStream.ReadAsync</c> returns 0 bytes when it elapses, so
    /// silence is noticed within half a second by the zero-length-read path below. A socket has no
    /// equivalent: <c>NetworkStream.ReadAsync</c> ignores <c>Socket.ReceiveTimeout</c> and simply
    /// waits, so without this window a device that stopped answering mid-file would keep the
    /// transfer parked until the caller's whole (30-minute) download budget expired.
    /// <para>
    /// The value is well clear of the longest legitimate gap the firmware can produce: its SD
    /// reply writer retries a full TCP buffer for up to ten seconds before it gives up on a chunk.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(20);

    private readonly Stream _sourceStream;
    private readonly int _bufferSize;
    private readonly bool _zeroLengthReadMeansClosed;
    private readonly TimeSpan? _idleTimeout;

    /// <summary>
    /// Default read buffer size, in bytes.
    /// </summary>
    private const int DefaultBufferSize = 16384;

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileReceiver"/> class for a transport
    /// that reports silence with a zero-length read — USB serial, where the port's own read
    /// timeout provides that signal.
    /// </summary>
    /// <param name="sourceStream">The transport stream to read raw bytes from.</param>
    /// <param name="bufferSize">Read buffer size in bytes. Defaults to 16384 (16 KB).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="bufferSize"/> is not positive.
    /// </exception>
    /// <remarks>
    /// Kept with its original signature rather than folded into the overload below as optional
    /// parameters: optional arguments are a compile-time convenience, so adding parameters here
    /// would change the CLR signature and leave already-compiled consumers of the
    /// <c>Daqifi.Core</c> package calling a constructor that no longer exists.
    /// </remarks>
    public SdCardFileReceiver(Stream sourceStream, int bufferSize = DefaultBufferSize)
        : this(sourceStream, zeroLengthReadMeansClosed: false, idleTimeout: null, bufferSize: bufferSize)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileReceiver"/> class, stating what the
    /// transport's silence means.
    /// </summary>
    /// <param name="sourceStream">The transport stream to read raw bytes from.</param>
    /// <param name="zeroLengthReadMeansClosed">
    /// Whether a zero-length read means the peer closed the connection. True for a stream-oriented
    /// network transport, where that is the only thing zero bytes can mean; false for USB serial,
    /// where it is the ordinary per-read-timeout signal from a device that is merely quiet. It
    /// selects between <see cref="SdCardTransferStallReason.TransportClosed"/> and
    /// <see cref="SdCardTransferStallReason.NoDataReceived"/> — the stream itself cannot tell them
    /// apart, because <c>NetworkStream.CanRead</c> stays true after the peer's FIN.
    /// </param>
    /// <param name="idleTimeout">
    /// How long to wait without receiving a byte before declaring the transfer stalled. Defaults
    /// to <see cref="DefaultIdleTimeout"/>; pass <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable the window and rely solely on the overall transfer deadline.
    /// </param>
    /// <param name="bufferSize">Read buffer size in bytes. Defaults to 16384 (16 KB).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="bufferSize"/> is not positive, or when
    /// <paramref name="idleTimeout"/> is neither positive nor
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public SdCardFileReceiver(
        Stream sourceStream,
        bool zeroLengthReadMeansClosed,
        TimeSpan? idleTimeout = null,
        int bufferSize = DefaultBufferSize)
    {
        _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be greater than zero.");
        }

        var effectiveIdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (effectiveIdleTimeout != Timeout.InfiniteTimeSpan && effectiveIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                effectiveIdleTimeout,
                "Idle timeout must be positive, or Timeout.InfiniteTimeSpan to disable it.");
        }

        _bufferSize = bufferSize;
        _zeroLengthReadMeansClosed = zeroLengthReadMeansClosed;
        _idleTimeout = effectiveIdleTimeout == Timeout.InfiniteTimeSpan
            ? null
            : effectiveIdleTimeout;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="listedFileSizeBytes">
    /// The size the device's directory listing reported for this file, when known. It is the only
    /// thing that separates a wedged SD subsystem from a genuinely 0-byte file, so a marker-only
    /// transfer raises <see cref="SdCardEmptyTransferException"/> only when this says the file is
    /// non-empty. Pass <c>0</c> for a listed empty file to have it return 0 bytes as a legitimate
    /// empty download. When <c>null</c> (no listing available) the conservative behavior is kept
    /// and a marker-only transfer throws.
    /// </param>
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
    // listedFileSizeBytes added AFTER cancellationToken (technically violates
    // CA1068 "CancellationToken should be last") to avoid breaking source compat
    // for any existing positional caller passing CancellationToken as the 5th
    // argument. Additivity wins over strict style here, matching the same call
    // made for UpdateWifiModuleAsync's skipVersionCheck in #143/PR #198.
#pragma warning disable CA1068
    public async Task<long> ReceiveAsync(
        Stream destinationStream,
        string fileName,
        IProgress<SdCardTransferProgress>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        long? listedFileSizeBytes = null)
#pragma warning restore CA1068
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

                // One inactivity window per read. Allocated per iteration rather than reset in
                // place because a CancellationTokenSource cannot be un-cancelled: reusing one
                // would turn a byte that arrived at the exact instant the window closed into a
                // permanently dead token for every read after it.
                using var idleCts = _idleTimeout is null
                    ? null
                    : CancellationTokenSource.CreateLinkedTokenSource(token);
                idleCts?.CancelAfter(_idleTimeout!.Value);

                try
                {
                    bytesRead = await _sourceStream.ReadAsync(buffer, 0, buffer.Length, idleCts?.Token ?? token)
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
                catch (OperationCanceledException ex) when (idleCts is { IsCancellationRequested: true } && !token.IsCancellationRequested)
                {
                    // Nothing arrived for the whole window while neither the caller nor the overall
                    // deadline had anything to say — the device stopped feeding the transfer. This
                    // is the only stall signal a socket gives (see DefaultIdleTimeout); on serial
                    // the zero-length-read path below gets there first and this never fires.
                    throw new SdCardTransferStalledException(
                        fileName,
                        totalBytesReceived,
                        SdCardTransferStallReason.NoDataReceived,
                        _idleTimeout,
                        ex);
                }

                // A read can complete without ever having observed the token: System.IO.Ports'
                // SerialStream ignores the CancellationToken once a read is in flight, so a
                // cancellation requested mid-read is only visible here. Checking it every
                // iteration is what makes the accepted token actually honored rather than
                // merely accepted (#399) — including on the zero-byte path below, which would
                // otherwise report a cancelled transfer as a timeout.
                token.ThrowIfCancellationRequested();

                if (bytesRead == 0)
                {
                    // A zero-byte read is NOT proof the stream closed on every transport. Over USB
                    // serial Core sets a per-read SerialPort.ReadTimeout and .NET's
                    // SerialStream.ReadAsync returns 0 on that timeout instead of throwing, so zero
                    // bytes is the ORDINARY stall signal there (#398 gap 1). On a socket it is the
                    // opposite: zero bytes is the peer's FIN and nothing else, and the stream will
                    // not admit it — NetworkStream.CanRead stays true until the stream is disposed,
                    // so the CanRead probe alone would report a closed connection as merely quiet.
                    // The caller, which knows its transport, settles it (#327).
                    var reason = _zeroLengthReadMeansClosed || !_sourceStream.CanRead
                        ? SdCardTransferStallReason.TransportClosed
                        : SdCardTransferStallReason.NoDataReceived;

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
