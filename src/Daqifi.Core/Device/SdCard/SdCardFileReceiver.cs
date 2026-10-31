using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Receives raw bytes from a transport stream during an SD card file download.
/// Accumulates data and writes it to a destination stream, detecting the terminal markers that
/// end the transfer: <c>__END_OF_FILE__</c> for a complete file, and — from firmware v3.7.3 —
/// <c>__TRANSFER_ERROR__</c> for a device-side read error part-way through.
/// </summary>
/// <remarks>
/// A third ending has no marker at all: firmware v3.7.3 still abandons a stalled peer after
/// partial data without sending a terminator, by design, so the inactivity window below remains
/// the only thing that ends such a transfer.
/// <para>
/// Both markers are ASCII text, so a log file can hold the same bytes. The listed file size is
/// what settles that: when the caller passes one, the receiver reads at least that many bytes of
/// content and the marker text inside them is file content like any other (#852).
/// </para>
/// </remarks>
public sealed class SdCardFileReceiver
{
    /// <summary>
    /// The ASCII marker appended by the firmware after all file data has been sent.
    /// </summary>
    internal static readonly byte[] EndOfFileMarker =
        Encoding.ASCII.GetBytes("__END_OF_FILE__");

    /// <summary>
    /// The ASCII marker appended by firmware v3.7.3 and later when a read from the card fails
    /// part-way through a transfer. Whatever preceded it is genuine but partial file content.
    /// </summary>
    internal static readonly byte[] TransferErrorMarker =
        Encoding.ASCII.GetBytes("__TRANSFER_ERROR__");

    /// <summary>
    /// The number of bytes held back from the destination stream while a marker could still be
    /// completing. It has to be the length of the LONGEST marker, not of whichever one is being
    /// looked for: a window sized for <see cref="EndOfFileMarker"/> would let the first bytes of
    /// a longer marker escape into the file before the rest of it arrived, and the tail of the
    /// download would be silently corrupted by a marker that was then never matched.
    /// </summary>
    internal static readonly int MaxMarkerLength =
        Math.Max(EndOfFileMarker.Length, TransferErrorMarker.Length);

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

    /// <summary>
    /// How long the receiver waits for more content after an <c>__END_OF_FILE__</c> marker when
    /// no listed size says where the file ends. Nothing arriving inside the window means the
    /// marker ended the file; file content arriving means the marker was part of the content and
    /// the end of the file cannot be located (<see cref="SdCardAmbiguousTransferException"/>).
    /// </summary>
    /// <remarks>
    /// It is short because the bytes it waits for are bytes the device has already written: a
    /// marker inside the content is followed by the next chunk of a file the device is streaming
    /// at full rate, not by a chunk it has yet to decide to send. The inactivity window above is
    /// the budget for a device that went quiet mid-file, which is a different question and two
    /// orders of magnitude longer; paying it after every marker would add twenty seconds to every
    /// download of a file with no listed size.
    /// </remarks>
    internal static readonly TimeSpan EndOfFileConfirmationWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How many reads the confirmation above may take. More than one is needed only because the
    /// device may follow the marker with a line ending, which can arrive in a read of its own;
    /// the cap keeps a transport that emits nothing but line endings from holding the call.
    /// </summary>
    private const int MaxEndOfFileConfirmationReads = 4;

    private readonly Stream _sourceStream;
    private readonly int _bufferSize;
    private readonly bool _zeroLengthReadMeansClosed;
    private readonly TimeSpan? _idleTimeout;

    /// <summary>
    /// Default read buffer size, in bytes.
    /// </summary>
    private const int DefaultBufferSize = 16384;

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileReceiver"/> class with the behavior
    /// it had before the inactivity window existed: silence is reported by a zero-length read (USB
    /// serial, where the port's own read timeout provides that signal), and the only thing that
    /// ends a quiet transfer is the caller's own deadline or cancellation.
    /// </summary>
    /// <param name="sourceStream">The transport stream to read raw bytes from.</param>
    /// <param name="bufferSize">Read buffer size in bytes. Defaults to 16384 (16 KB).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="bufferSize"/> is not positive.
    /// </exception>
    /// <remarks>
    /// This overload exists for consumers compiled against an earlier <c>Daqifi.Core</c>, so it
    /// preserves what they were compiled against on <b>both</b> axes. Keeping the signature is the
    /// obvious half: optional arguments are a compile-time convenience, so adding parameters to it
    /// would change the CLR signature and leave those consumers calling a constructor that no
    /// longer exists. Keeping the semantics is the half that is easy to lose — delegating with the
    /// new default would switch on a 20-second inactivity window that no such caller ever asked
    /// for, and any transport that can go quiet without returning zero bytes would start being
    /// abandoned mid-transfer. A silent behavior change is worse than the loud binary break this
    /// overload exists to prevent, so the window is explicitly off here.
    /// <para>
    /// Use the overload below to opt into it.
    /// </para>
    /// </remarks>
    public SdCardFileReceiver(Stream sourceStream, int bufferSize = DefaultBufferSize)
        : this(
            sourceStream,
            zeroLengthReadMeansClosed: false,
            idleTimeout: Timeout.InfiniteTimeSpan,
            bufferSize: bufferSize)
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
    /// Reads bytes from the source stream until a terminal marker is detected, writing all file
    /// content (minus the marker) to the destination stream. <c>__END_OF_FILE__</c> completes the
    /// download; <c>__TRANSFER_ERROR__</c> ends it with
    /// <see cref="SdCardTransferErrorException"/>.
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
    /// The size the device's directory listing reported for this file, when known. It is the
    /// authority on how long the file is, because the EOF marker is the firmware's only completion
    /// signal and says nothing about whether the whole file arrived: the receiver copies at least
    /// this many bytes and reads the marker text inside them as the file content it is, a transfer
    /// that ends short of it raises <see cref="SdCardTruncatedTransferException"/>, and one with no
    /// content bytes at all raises <see cref="SdCardEmptyTransferException"/>. It is also the only
    /// thing that separates a wedged SD subsystem from a genuinely 0-byte file, so pass <c>0</c>
    /// for a listed empty file to have it return 0 bytes as a legitimate empty download. When
    /// <c>null</c> (no listing available) there is nothing to judge the length against: a short
    /// transfer cannot be detected, and a file whose own content holds the marker text raises
    /// <see cref="SdCardAmbiguousTransferException"/> instead of returning a truncated file.
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
    /// <exception cref="SdCardTransferErrorException">
    /// Thrown when the device ends the transfer with the <c>__TRANSFER_ERROR__</c> marker,
    /// meaning it failed to read the rest of the file off the card. Whatever arrived before the
    /// marker has been written to <paramref name="destinationStream"/> and is genuine, partial
    /// file content.
    /// </exception>
    /// <exception cref="SdCardTruncatedTransferException">
    /// Thrown when the EOF marker arrives after fewer content bytes than
    /// <paramref name="listedFileSizeBytes"/> reports for the file, meaning the device served a
    /// short reply — an error line, or a partial file — rather than the file itself. Whatever
    /// arrived has already been written to <paramref name="destinationStream"/> and must be
    /// discarded.
    /// </exception>
    /// <exception cref="SdCardAmbiguousTransferException">
    /// Thrown when the file's own content holds the <c>__END_OF_FILE__</c> text, more content
    /// follows it, and <paramref name="listedFileSizeBytes"/> is <c>null</c>. The end of the file
    /// cannot be located, so what was written to <paramref name="destinationStream"/> is genuine
    /// but partial content and must be discarded; downloading the file with a listed size
    /// available works.
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

        // We keep a trailing window of the last N bytes to detect a terminal marker
        // even when it's split across chunk boundaries.
        var trailingBytes = new byte[MaxMarkerLength];
        var trailingCount = 0;

        // Where the first EOF marker that arrived BELOW the listed size was, measured in content
        // bytes. Such a marker is file content as far as this transfer is concerned (the listed
        // size says the file is longer), so the transfer carries on past it — but if the device
        // then sends nothing more, that marker was the end of a short reply after all, and this
        // is the byte count to report it with (#539).
        long? shortReplyContentLength = null;

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
                    ThrowIfShortReplyEnded(fileName, shortReplyContentLength, listedFileSizeBytes);

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
                    ThrowIfShortReplyEnded(fileName, shortReplyContentLength, listedFileSizeBytes);

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

                    ThrowIfShortReplyEnded(fileName, shortReplyContentLength, listedFileSizeBytes);

                    throw new SdCardTransferStalledException(fileName, totalBytesReceived, reason);
                }

                // The listed size is a floor for the EOF marker: the marker is ASCII text, so a log
                // file can contain it, and below the floor it is the file's own content rather than
                // a terminator (#852). Everything before the trailing window has been written, so
                // the window starts at totalBytesReceived and a marker's content offset is
                // totalBytesReceived + its position in the combined view.
                var eofFloor = 0;
                if (listedFileSizeBytes is { } listedSize)
                {
                    var bytesStillOwed = listedSize - totalBytesReceived;
                    eofFloor = bytesStillOwed <= 0
                        ? 0
                        : (int)Math.Min(bytesStillOwed, trailingCount + bytesRead);
                }

                // Check if a terminal marker is contained in or spans the new data
                var (foundMarker, markerPosition, isTransferError, subFloorEofPosition) =
                    FindTerminalMarker(trailingBytes, trailingCount, buffer, bytesRead, eofFloor);

                if (subFloorEofPosition >= 0 && shortReplyContentLength is null)
                {
                    shortReplyContentLength = totalBytesReceived + subFloorEofPosition;
                }

                if (foundMarker)
                {
                    // Write only the data bytes before the marker
                    // markerPosition is relative to the combined [trailing + new] buffer.
                    // The data we haven't written yet from trailing is trailingCount bytes,
                    // so we need to figure out how much of the new data is actual file content.
                    var newDataFileBytes = markerPosition - trailingCount;

                    if (newDataFileBytes < 0)
                    {
                        // The marker started within the trailing bytes. We only need to
                        // write the portion of the trailing buffer before the marker.
                        var trailingToWrite = markerPosition;
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

                    if (isTransferError)
                    {
                        // The device gave up reading the card mid-file. Everything written so far
                        // is real content, so it is left in the destination stream for a caller
                        // that can use a partial log — but the download is over and incomplete,
                        // and saying so is the whole point of the marker: before firmware v3.7.3
                        // this path sent nothing and surfaced as a NoDataReceived stall, blaming
                        // the transport for a card read error (#720).
                        throw new SdCardTransferErrorException(fileName, totalBytesReceived);
                    }

                    if (listedFileSizeBytes is null)
                    {
                        if (totalBytesReceived == 0)
                        {
                            // An immediate EOF marker with no preceding file bytes is ambiguous on
                            // the wire: a transient/wedged SD subsystem opens the file and sends
                            // nothing (#264), and so does a genuinely 0-byte file left behind by an
                            // interrupted logging session. With no listed size to tell them apart
                            // we keep the conservative #264 behavior so a wedged subsystem is still
                            // caught (#398 gap 2). There is also nothing to confirm below: with no
                            // content before the marker there is no content to truncate, and the
                            // caller's answer is the same retry either way.
                            throw new SdCardEmptyTransferException(fileName, null);
                        }

                    }

                    // Even a listed size can be stale if another session appended to the file.
                    // A marker at or past that size is not a terminator when content follows it.
                    // Confirm both sized and unsized transfers before reporting a complete file.
                    await ConfirmMarkerEndedTheFileAsync(
                        buffer,
                        markerPosition + EndOfFileMarker.Length - trailingCount,
                        bytesRead,
                        fileName,
                        totalBytesReceived,
                        cancellationToken,
                        token).ConfigureAwait(false);

                    // With a listed size the floor above has already settled both of the checks
                    // this used to make here: the marker was only accepted at or past the listed
                    // size, so the transfer cannot be short (#539) or marker-only (#264) — a
                    // marker below the floor was taken as content and recorded in
                    // shortReplyContentLength, and the stall paths report it if the device then
                    // sends nothing more. A transfer LONGER than the listed size still succeeds:
                    // a listing goes stale the moment an active logging session appends to the
                    // file, and those extra bytes are real content.
                    progress?.Report(new SdCardTransferProgress(totalBytesReceived, fileName));
                    return totalBytesReceived;
                }

                // No terminal marker found — combine trailing + new data, write
                // everything except the last MaxMarkerLength bytes (the new trailing window).
                var combinedLen = trailingCount + bytesRead;

                if (combinedLen >= MaxMarkerLength)
                {
                    // How many bytes can we safely write (not part of potential marker)?
                    var safeCount = combinedLen - MaxMarkerLength;

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

                    // Build the new trailing window from the last MaxMarkerLength bytes of [trailing + new]
                    var newTrailingCount = MaxMarkerLength;
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
            ThrowIfShortReplyEnded(fileName, shortReplyContentLength, listedFileSizeBytes);

            throw new SdCardTransferStalledException(
                fileName,
                totalBytesReceived,
                SdCardTransferStallReason.TransferTimeout,
                effectiveTimeout,
                ex);
        }
    }

    /// <summary>
    /// Reports a transfer that ended at an EOF marker which arrived below the listed size. The
    /// marker was treated as file content while the transfer continued, because the listing says
    /// the file is longer; the device sending nothing after it means the reply really did end
    /// there, and the byte count to report is the content that preceded that marker, not the
    /// marker and whatever followed it.
    /// </summary>
    /// <param name="fileName">The file being downloaded.</param>
    /// <param name="shortReplyContentLength">
    /// Content bytes before the first sub-floor marker, or <c>null</c> when there was none — in
    /// which case this returns and the caller reports the stall itself.
    /// </param>
    /// <param name="listedFileSizeBytes">
    /// The listed size. Never <c>null</c> when <paramref name="shortReplyContentLength"/> has a
    /// value, because only a listed size creates a floor for a marker to fall below.
    /// </param>
    /// <exception cref="SdCardEmptyTransferException">
    /// Thrown when the reply held no content at all before the marker (#264).
    /// </exception>
    /// <exception cref="SdCardTruncatedTransferException">
    /// Thrown when it held some content but less than the listing reports (#539).
    /// </exception>
    private static void ThrowIfShortReplyEnded(
        string fileName, long? shortReplyContentLength, long? listedFileSizeBytes)
    {
        if (shortReplyContentLength is not { } contentLength || listedFileSizeBytes is not { } listedSize)
        {
            return;
        }

        if (contentLength == 0)
        {
            throw new SdCardEmptyTransferException(fileName, listedSize);
        }

        throw new SdCardTruncatedTransferException(fileName, listedSize, contentLength);
    }

    /// <summary>
    /// Decides whether an <c>__END_OF_FILE__</c> marker ended the file, for a transfer with no
    /// listed size to measure against. Returns when nothing but line endings follows the marker;
    /// throws when file content does.
    /// </summary>
    /// <param name="buffer">
    /// The read buffer the marker was found in. Everything the marker did not cover has already
    /// been accounted for, so this method reuses it for its own reads.
    /// </param>
    /// <param name="leftoverStart">Index in <paramref name="buffer"/> just past the marker.</param>
    /// <param name="leftoverEnd">Index in <paramref name="buffer"/> just past the last byte read.</param>
    /// <param name="fileName">The file being downloaded.</param>
    /// <param name="contentLength">File bytes received before the marker.</param>
    /// <param name="cancellationToken">The caller's own token; only it can fail this method.</param>
    /// <param name="token">The token the transfer reads with (caller plus overall deadline).</param>
    /// <exception cref="SdCardAmbiguousTransferException">
    /// Thrown when file content follows the marker.
    /// </exception>
    private async Task ConfirmMarkerEndedTheFileAsync(
        byte[] buffer,
        int leftoverStart,
        int leftoverEnd,
        string fileName,
        long contentLength,
        CancellationToken cancellationToken,
        CancellationToken token)
    {
        // The cheapest answer first: bytes already read that the marker did not cover.
        if (HoldsFileContent(buffer, leftoverStart, leftoverEnd))
        {
            throw new SdCardAmbiguousTransferException(fileName, contentLength);
        }

        var window = _idleTimeout is { } idle && idle < EndOfFileConfirmationWindow
            ? idle
            : EndOfFileConfirmationWindow;

        for (var read = 0; read < MaxEndOfFileConfirmationReads; read++)
        {
            int bytesRead;

            using var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            confirmCts.CancelAfter(window);

            try
            {
                bytesRead = await _sourceStream.ReadAsync(buffer, 0, buffer.Length, confirmCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The window closed on a quiet transport, or the overall deadline ran out while it
                // was quiet. Either way nothing followed the marker, and the file is complete.
                return;
            }
            catch (IOException)
            {
                // The transport failed after the file had already arrived. The content stands.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (bytesRead == 0)
            {
                // End of stream, or a serial per-read timeout: the device has nothing more to say.
                return;
            }

            if (HoldsFileContent(buffer, 0, bytesRead))
            {
                throw new SdCardAmbiguousTransferException(fileName, contentLength);
            }
        }
    }

    /// <summary>
    /// Says whether a region holds anything that could be file content. A carriage return or line
    /// feed after the marker is the device ending its reply line, which the Python port accepts the
    /// same way; anything else is content, and content after the marker means the marker was
    /// content too.
    /// </summary>
    private static bool HoldsFileContent(byte[] buffer, int start, int end)
    {
        for (var i = Math.Max(0, start); i < end; i++)
        {
            if (buffer[i] != (byte)'\r' && buffer[i] != (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Searches for a terminal marker in the combined view of trailing bytes and new data.
    /// A marker could span the boundary between the two buffers.
    /// </summary>
    /// <returns>
    /// A tuple of (found, position, isTransferError) where position is the start index of the
    /// marker in the virtual combined buffer [trailing..new], and isTransferError says which
    /// marker matched. When both markers match, the earlier one wins — the two cannot start at
    /// the same position, since they differ from their third byte on.
    /// </returns>
    internal static (bool Found, int Position, bool IsTransferError) FindTerminalMarker(
        byte[] trailing, int trailingCount, byte[] newData, int newDataLength)
    {
        var (found, position, isTransferError, _) = FindTerminalMarker(
            trailing, trailingCount, newData, newDataLength, eofFloor: 0);

        return (found, position, isTransferError);
    }

    /// <summary>
    /// Searches for a terminal marker, with <paramref name="eofFloor"/> saying how far into the
    /// combined view the file's own content is known to reach. An <c>__END_OF_FILE__</c> marker
    /// below that point is file content, not a terminator, and is reported separately instead of
    /// ending the transfer (#852).
    /// </summary>
    /// <returns>
    /// The tuple the four-argument overload returns, plus the position of the first EOF marker
    /// found below <paramref name="eofFloor"/>, or <c>-1</c> when there was none.
    /// </returns>
    /// <remarks>
    /// The floor applies to the EOF marker only. <c>__TRANSFER_ERROR__</c> says the device gave up
    /// reading the card part-way through the file, so by definition it arrives before the listed
    /// size is reached; holding it to the same floor would turn every mid-file card read error back
    /// into the transport stall it looked like before firmware v3.7.3 (#720).
    /// </remarks>
    internal static (bool Found, int Position, bool IsTransferError, int SubFloorEofPosition)
        FindTerminalMarker(
            byte[] trailing, int trailingCount, byte[] newData, int newDataLength, int eofFloor)
    {
        var (foundEof, eofPosition) = FindMarker(
            EndOfFileMarker, trailing, trailingCount, newData, newDataLength);

        var subFloorEofPosition = -1;
        if (foundEof && eofPosition < eofFloor)
        {
            // File content that happens to read as the marker. Another marker can still follow it
            // inside this same chunk, so keep looking from the floor on.
            subFloorEofPosition = eofPosition;
            (foundEof, eofPosition) = FindMarker(
                EndOfFileMarker, trailing, trailingCount, newData, newDataLength, eofFloor);
        }

        var (foundError, errorPosition) = FindMarker(
            TransferErrorMarker, trailing, trailingCount, newData, newDataLength);

        if (foundEof && foundError)
        {
            return errorPosition < eofPosition
                ? (true, errorPosition, true, subFloorEofPosition)
                : (true, eofPosition, false, subFloorEofPosition);
        }

        if (foundEof)
        {
            return (true, eofPosition, false, subFloorEofPosition);
        }

        return foundError
            ? (true, errorPosition, true, subFloorEofPosition)
            : (false, -1, false, subFloorEofPosition);
    }

    /// <summary>
    /// Searches for one specific marker in the combined view of trailing bytes and new data.
    /// </summary>
    /// <param name="marker">The marker to look for.</param>
    /// <param name="trailing">The bytes held back from the previous read.</param>
    /// <param name="trailingCount">How many of <paramref name="trailing"/> are in use.</param>
    /// <param name="newData">The read buffer.</param>
    /// <param name="newDataLength">How many bytes of <paramref name="newData"/> were read.</param>
    /// <param name="minPosition">
    /// The earliest position in the combined view a match may start at. Used to look past a match
    /// that the listed size has already shown to be file content.
    /// </param>
    /// <returns>
    /// A tuple of (found, position) where position is the start index of the marker
    /// in the virtual combined buffer [trailing..new].
    /// </returns>
    private static (bool Found, int Position) FindMarker(
        byte[] marker,
        byte[] trailing,
        int trailingCount,
        byte[] newData,
        int newDataLength,
        int minPosition = 0)
    {
        if (trailingCount + newDataLength < marker.Length)
        {
            return (false, -1);
        }

        // Build a search window that covers the overlap zone.
        // The marker can start at any position from (trailingCount - markerLength + 1)
        // to (trailingCount + newDataLength - markerLength).
        // But we only need to scan positions that include new data to avoid re-scanning.
        var markerLen = marker.Length;
        var searchStart = Math.Max(minPosition, Math.Max(0, trailingCount - markerLen + 1));
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

                if (b != marker[j])
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
