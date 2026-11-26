using Daqifi.Core.Communication.Producers;
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
/// The device can also answer with no file at all: <c>Error !! No SD Card Detected</c> and its
/// error line, sent while it believes no card is present. The first bytes of every transfer are
/// held back until they show whether the reply opens with that answer, which ends the transfer
/// with <see cref="SdCardNotPresentException"/> and leaves the destination untouched (#1077).
/// </para>
/// <para>
/// Both markers are ASCII text, so a log file can hold the same bytes. The listed file size is
/// what settles that: when the caller passes one, the receiver reads at least that many bytes of
/// content and the marker text inside them is file content like any other (#852).
/// </para>
/// <para>
/// A listed size of <c>0</c> is the one case where nothing is owed at all. Such a transfer cannot
/// stall, because there is no content left for the device to send: it completes with an empty
/// stream however the device ends its reply, and the inactivity window never applies to it (#888).
/// </para>
/// </remarks>
internal sealed class SdCardFileReceiver
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

    /// <summary>
    /// How long a transfer whose listing reports the file as empty waits for another byte, once
    /// the device's reply has started arriving, before it reports the empty file as complete.
    /// </summary>
    /// <remarks>
    /// Nothing is owed on such a transfer, so this window is not a stall budget — it is how long
    /// the terminator is given to turn up behind the reply that is already arriving, and the
    /// firmware puts the two in the same reply. The inactivity window above is the budget for a
    /// device that went quiet with file content still outstanding, which cannot be the case here.
    /// <para>
    /// It deliberately does not shorten the wait for the FIRST byte: a device that has not
    /// answered at all is a different question, and cutting that short would risk concluding the
    /// transfer while the reply — terminator and all — was still in flight, leaving it in the
    /// transport buffer for the next exchange to trip over.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan ListedEmptyFileQuietWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many bytes of a listed-empty file's transfer are held in memory, rather than written to
    /// the destination stream, while it is still unknown whether they are file content at all.
    /// </summary>
    /// <remarks>
    /// A listing that reports 0 bytes says every byte before the terminator is reply framing, not
    /// content — but a listing can be stale, so bytes that a terminator then confirms as content
    /// must still reach the caller. Holding them until the terminator settles it is what lets the
    /// empty case deliver an empty stream instead of the framing around it.
    /// <para>
    /// The limit is sized as framing, not as a buffer: the device's reply to an SD <c>GET</c> is a
    /// command echo and a prompt, measured at 48 bytes over WiFi and 79 with the echo on. Anything
    /// substantially larger than that is not framing, so the listing is stale and a real transfer
    /// is under way: the bytes are released to the destination and the transfer behaves like any
    /// other from then on, inactivity window included. Keeping the limit this small is what stops
    /// a listed-empty transfer from ever discarding a meaningful amount of real content.
    /// </para>
    /// </remarks>
    internal const int ListedEmptyFileReplyFramingLimitBytes = 256;

    /// <summary>
    /// The text of the device's answer to an SD <c>GET</c> when it believes no card is present,
    /// sent in place of the file (#1077).
    /// </summary>

    /// <summary>
    /// How many bytes at the start of a transfer are held back from the destination stream while
    /// they could still be the device's no-card answer rather than file content.
    /// </summary>
    /// <remarks>
    /// The answer is 66 bytes as the bench device sends it, plus the command echo and prompt when
    /// echo is on. A transfer that has sent more than this without opening with the answer is a
    /// real transfer, and its bytes are released and written as they arrive from then on.
    /// </remarks>
    internal const int ReplyHeadLimitBytes = 256;

    /// <summary>
    /// The most raw bytes the opening of a reply is ever inspected over, framing included. The
    /// command echo carries the file name, which has no length limit of its own, so the limit
    /// above counts only what follows the framing; this one bounds the whole probe.
    /// </summary>
    internal const int MaxReplyProbeBytes = 4096;

    /// <summary>The device's no-card line, exactly as the firmware prints it.</summary>
    private const string NoSdCardAnswerLine = "Error !! No SD Card Detected";

    /// <summary>The prompt the firmware prints ahead of a command echo when echo is on.</summary>
    private const string PromptPrefix = "DAQIFI>";

    /// <summary>A SCPI error line as the firmware prints it: <c>**ERROR: -200, "Execution error"</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex ScpiErrorLine = new(
        @"^\*\*ERROR:\s*-?\d+\s*,\s*"".*""$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
    /// for a listed empty file to have it return 0 bytes as a legitimate empty download —
    /// whichever way the device ends its reply, including by simply going quiet, since a file with
    /// no content cannot stall part-way through (#888). When
    /// <c>null</c> (no listing available) there is nothing to judge the length against: a short
    /// transfer cannot be detected, and a file whose own content holds the marker text raises
    /// <see cref="SdCardAmbiguousTransferException"/> instead of returning a truncated file.
    /// </param>
    /// <returns>The total number of file bytes written (excluding the EOF marker).</returns>
    /// <exception cref="SdCardTransferStalledException">
    /// Thrown when the transfer stops making progress before the EOF marker arrives — the
    /// transport went quiet, closed, or the timeout elapsed. Inspect
    /// <see cref="SdCardTransferStalledException.Reason"/> to tell those apart. Never thrown for a
    /// file <paramref name="listedFileSizeBytes"/> reports as empty, which has no content left to
    /// stall on and completes with an empty stream instead (#888).
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

        // A listing that reports 0 bytes says this file has no content, so until a terminal marker
        // proves otherwise every byte that arrives is the device's reply framing rather than file
        // content, and must not reach the caller's stream: that is what lets an empty log download
        // as an empty file instead of as the framing around it (#888). A stale listing is still
        // honored — a marker commits whatever preceded it, and anything bigger than reply framing is
        // released and the transfer carries on as any other.
        //
        // Every other transfer holds back its first bytes too, for a different reason: until they
        // are known not to be the device's "No SD Card Detected" answer, they may not be file
        // content at all, and the caller's stream must not receive them (#1077).
        var listedEmpty = listedFileSizeBytes is 0;
        using var heldContent = new HeldContentStream(
            destinationStream,
            listedEmpty ? ListedEmptyFileReplyFramingLimitBytes : ReplyHeadLimitBytes);
        var contentTarget = (Stream)heldContent;

        // While the opening is still being inspected, nothing is released however much is held:
        // the limit could otherwise be crossed (a long echoed file name, one-byte reads) before
        // the answer's error line completes, and a retry would append the file to those bytes.
        heldContent.HoldUntilReleased = true;

        // The raw bytes the reply opened with, kept until they settle whether it is the no-card
        // answer; null once they have settled that it is not.
        var replyHead = new MemoryStream();

        // What echo repeats when it is on: the GET exactly as it went out.
        var expectedEcho = ExpectedEcho(fileName);

        // Set when the reply opened with the no-card line. The transfer then ends at the first
        // quiet moment instead of after the inactivity window, since the device sends nothing
        // more after that answer.
        var noCardAnswerSeen = false;

        // Set once any byte of the device's reply has arrived. Only then does the quiet window for
        // a listed-empty file apply; see ListedEmptyFileQuietWindow.
        var replyStarted = false;

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

        // Every way the transfer can end without a terminal marker — the device going quiet, the
        // transport closing, the overall deadline — comes through here, so they all apply the same
        // rule. A listed-empty file whose held bytes never grew past reply framing is the complete,
        // zero-byte download it is, and the framing no marker ever claimed as content is discarded:
        // with nothing outstanding there is nothing to stall on, and the 20-second wait plus a
        // message telling the user to power-cycle healthy hardware was the whole of issue #888.
        // Anything else is a short reply that has now ended (#539), or a stall.
        //
        // A null reason means the exit was a zero-length read, whose reason depends on the
        // transport and is only worked out once a stall is actually being reported: probing the
        // stream for a download that has already completed could fail it after the fact, since a
        // disconnected stream's CanRead may throw.
        async Task<long> EndWithoutTerminatorAsync(
            SdCardTransferStallReason? reason, TimeSpan? window = null, Exception? cause = null)
        {
            if (noCardAnswerSeen && !heldContent.HasReleasedContent)
            {
                throw NoCardAnswer(heldContent, replyHead);
            }

            await HoldProbedBytesAsContentAsync().ConfigureAwait(false);

            if (listedEmpty && !heldContent.HasReleasedContent)
            {
                heldContent.DiscardHeldBytes();
                progress?.Report(new SdCardTransferProgress { BytesReceived = 0, FileName = fileName });
                return 0;
            }

            // Whatever is still held back was content after all: it goes to the caller's stream,
            // as everything outside the marker window always has on a transfer that ended early.
            // Under the live token, so a destination that blocks cannot outlast the caller's
            // cancellation; and not at all once the deadline has passed, rather than starting an
            // unbounded write after it. At most the probe's few hundred bytes are lost that way.
            await ReleaseHeldContentIfStillLiveAsync(heldContent, token, cancellationToken).ConfigureAwait(false);

            ThrowIfShortReplyEnded(fileName, shortReplyContentLength, listedFileSizeBytes);

            // A zero-byte read is NOT proof the stream closed on every transport. Over USB serial
            // Core sets a per-read SerialPort.ReadTimeout and .NET's SerialStream.ReadAsync returns
            // 0 on that timeout instead of throwing, so zero bytes is the ORDINARY stall signal
            // there (#398 gap 1). On a socket it is the opposite: zero bytes is the peer's FIN and
            // nothing else, and the stream will not admit it — NetworkStream.CanRead stays true
            // until the stream is disposed, so the CanRead probe alone would report a closed
            // connection as merely quiet. The caller, which knows its transport, settles it (#327).
            reason ??= _zeroLengthReadMeansClosed || !_sourceStream.CanRead
                ? SdCardTransferStallReason.TransportClosed
                : SdCardTransferStallReason.NoDataReceived;

            throw new SdCardTransferStalledException(fileName, totalBytesReceived, reason.Value, window, cause);
        }

        // An opening still being classified when the transfer ends some other way than with the
        // no-card answer was file content after all. Its bytes join what is held back for the
        // destination, outside the marker window as every early end leaves it, and are counted
        // only while the transfer is still live to write them.
        async Task HoldProbedBytesAsContentAsync()
        {
            if (listedEmpty || noCardAnswerSeen || replyHead is not { Length: > 0 } probed || token.IsCancellationRequested)
            {
                return;
            }

            var (_, _, echo) = ClassifyReplyHead(probed.GetBuffer().AsSpan(0, (int)probed.Length), expectedEcho);
            var content = (int)Math.Max(0, probed.Length - echo - MaxMarkerLength);
            await heldContent.WriteAsync(probed.GetBuffer(), echo, content, token).ConfigureAwait(false);
            totalBytesReceived += content;
        }

        try
        {
            while (true)
            {
                int bytesRead;

                // A listed-empty file whose reply has started arriving waits only the short quiet
                // window for the terminator behind it, and takes that window's expiry as the
                // completion it is rather than as a stall. Once content has been released to the
                // destination the listing was stale, a real transfer is under way, and the
                // ordinary inactivity window is the right one again.
                //
                // The same short window ends a reply that opened with the no-card answer: the
                // device sends nothing after it, so waiting longer only delays the answer.
                var idleWindow = _idleTimeout;
                if (replyStarted
                    && !heldContent.HasReleasedContent
                    && (listedEmpty || noCardAnswerSeen))
                {
                    idleWindow = idleWindow is { } configured && configured < ListedEmptyFileQuietWindow
                        ? configured
                        : ListedEmptyFileQuietWindow;
                }

                // One inactivity window per read. Allocated per iteration rather than reset in
                // place because a CancellationTokenSource cannot be un-cancelled: reusing one
                // would turn a byte that arrived at the exact instant the window closed into a
                // permanently dead token for every read after it.
                using var idleCts = idleWindow is null
                    ? null
                    : CancellationTokenSource.CreateLinkedTokenSource(token);
                idleCts?.CancelAfter(idleWindow!.Value);

                try
                {
                    bytesRead = await _sourceStream.ReadAsync(buffer, 0, buffer.Length, idleCts?.Token ?? token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (
                    idleCts is { IsCancellationRequested: true }
                    && !token.IsCancellationRequested
                    && !timeoutCts.IsCancellationRequested)
                {
                    // Nothing arrived for the whole window while neither the caller nor the overall
                    // deadline had anything to say — the device stopped feeding the transfer. This
                    // is the only stall signal a socket gives (see DefaultIdleTimeout); on serial
                    // the zero-length-read path below gets there first and this never fires. An
                    // expired overall deadline is left to the catch around the whole loop, which
                    // reports it as the timeout it is. The deadline check is not redundant with
                    // `token`: the deadline's own source is marked cancelled a moment before its
                    // cancellation reaches the linked token, and an idle window expiring in that
                    // moment must still be reported as the deadline.
                    return await EndWithoutTerminatorAsync(SdCardTransferStallReason.NoDataReceived, idleWindow, ex).ConfigureAwait(false);
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
                    // No reason given: whether zero bytes means quiet or closed is settled inside
                    // the helper, and only if a stall is actually being reported.
                    return await EndWithoutTerminatorAsync(reason: null).ConfigureAwait(false);
                }

                replyStarted = true;

                // What this iteration hands to the marker scan and the destination: the read, or,
                // on the read that settles the opening as content, every byte held while it was
                // being classified.
                var data = buffer;
                var count = bytesRead;

                if (replyHead is not null)
                {
                    replyHead.Write(buffer, 0, bytesRead);

                    var (kind, framingBytes, echoBytes) = ClassifyReplyHead(
                        replyHead.GetBuffer().AsSpan(0, (int)replyHead.Length), expectedEcho);

                    // Bounded however the bytes arrive: past the limit after the framing, or past
                    // the whole probe, the opening is not the answer.
                    if (kind is ReplyHeadKind.Undecided or ReplyHeadKind.NoCardLine or ReplyHeadKind.NoCardAnswer
                        && (replyHead.Length - framingBytes > ReplyHeadLimitBytes || replyHead.Length > MaxReplyProbeBytes))
                    {
                        kind = ReplyHeadKind.Content;
                    }

                    switch (kind)
                    {
                        case ReplyHeadKind.NoCardAnswer:
                        case ReplyHeadKind.NoCardLine:
                            // Tentative until the reply goes quiet: the short quiet window then
                            // ends it as the answer, while anything more that arrives first is
                            // classified again with it.
                            noCardAnswerSeen = true;
                            break;
                        case ReplyHeadKind.Content:
                            noCardAnswerSeen = false;
                            data = replyHead.ToArray();
                            count = data.Length;
                            replyHead.Dispose();
                            replyHead = null;
                            break;
                    }

                    if (replyHead is not null)
                    {
                        // Still being classified, so nothing about these bytes is decided yet:
                        // they are not scanned for a marker (one can be spelled inside the echoed
                        // file name), not written, and not reported as progress (#1078).
                        continue;
                    }

                    // The echoed GET, and anything ahead of it, is the device repeating Core's
                    // command: not file data, so it is neither written, counted against the
                    // listed size, nor scanned for a marker (one can be spelled in the file name).
                    // Blank lines after it are kept: they can be the file's own.
                    if (echoBytes > 0)
                    {
                        data = data[echoBytes..];
                        count = data.Length;
                    }
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
                        : (int)Math.Min(bytesStillOwed, trailingCount + count);
                }

                // Check if a terminal marker is contained in or spans the new data
                var (foundMarker, markerPosition, isTransferError, subFloorEofPosition) =
                    FindTerminalMarker(trailingBytes, trailingCount, data, count, eofFloor);

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
                            await contentTarget.WriteAsync(trailingBytes, 0, trailingToWrite, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += trailingToWrite;
                        }
                    }
                    else
                    {
                        // Write deferred trailing bytes
                        if (trailingCount > 0)
                        {
                            await contentTarget.WriteAsync(trailingBytes, 0, trailingCount, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += trailingCount;
                        }

                        // Write the portion of new data before the marker
                        if (newDataFileBytes > 0)
                        {
                            await contentTarget.WriteAsync(data, 0, newDataFileBytes, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += newDataFileBytes;
                        }
                    }

                    // A terminator settles what the held bytes of a listed-empty transfer were:
                    // the listing was stale and they are file content, so they go to the caller's
                    // stream now. This has to happen before the two throws below, both of which
                    // document the content that arrived as left in the destination stream.
                    await heldContent.ReleaseHeldBytesAsync(token).ConfigureAwait(false);

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
                        data,
                        markerPosition + EndOfFileMarker.Length - trailingCount,
                        count,
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
                    progress?.Report(new SdCardTransferProgress { BytesReceived = totalBytesReceived, FileName = fileName });
                    return totalBytesReceived;
                }

                // No terminal marker found — combine trailing + new data, write
                // everything except the last MaxMarkerLength bytes (the new trailing window).
                var combinedLen = trailingCount + count;

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
                            await contentTarget.WriteAsync(trailingBytes, 0, fromTrailing, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += fromTrailing;
                        }

                        // Write from the new data
                        var fromNew = safeCount - fromTrailing;
                        if (fromNew > 0)
                        {
                            await contentTarget.WriteAsync(data, 0, fromNew, token)
                                .ConfigureAwait(false);
                            totalBytesReceived += fromNew;
                        }
                    }

                    // Build the new trailing window from the last MaxMarkerLength bytes of [trailing + new]
                    var newTrailingCount = MaxMarkerLength;
                    var fromNewData = Math.Min(count, newTrailingCount);
                    var fromTrailingData = newTrailingCount - fromNewData;

                    if (fromTrailingData > 0)
                    {
                        Array.Copy(trailingBytes, trailingCount - fromTrailingData, trailingBytes, 0, fromTrailingData);
                    }

                    if (fromNewData > 0)
                    {
                        Array.Copy(data, count - fromNewData, trailingBytes, fromTrailingData, fromNewData);
                    }

                    trailingCount = newTrailingCount;
                }
                else
                {
                    // Combined data is still shorter than the marker — just accumulate
                    Array.Copy(data, 0, trailingBytes, trailingCount, count);
                    trailingCount = combinedLen;
                }

                if (replyHead is null && heldContent.HoldUntilReleased)
                {
                    // The opening has been settled as content. A listed-empty transfer goes back
                    // to its framing limit; any other writes what it holds now, so bytes known to
                    // be file content are in the caller's stream even if the deadline comes next.
                    if (listedEmpty)
                    {
                        await heldContent.StopHoldingAsync(token).ConfigureAwait(false);
                    }
                    else
                    {
                        heldContent.HoldUntilReleased = false;
                        await heldContent.ReleaseHeldBytesAsync(token).ConfigureAwait(false);
                    }
                }

                progress?.Report(new SdCardTransferProgress { BytesReceived = totalBytesReceived, FileName = fileName });
            }
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await EndWithoutTerminatorAsync(SdCardTransferStallReason.TransferTimeout, effectiveTimeout, ex).ConfigureAwait(false);
        }
        catch (Exception) when (!listedEmpty && !heldContent.HasReleasedContent)
        {
            // A transport failure or a cancellation part-way through: bytes held back only to
            // rule out the no-card answer were real content, and they reach the caller's stream
            // as they did before the hold existed. A no-card answer has been discarded already.
            try
            {
                await HoldProbedBytesAsContentAsync().ConfigureAwait(false);
                await ReleaseHeldContentIfStillLiveAsync(heldContent, token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The exception being handled is the one to report.
            }

            throw;
        }
        finally
        {
            replyHead?.Dispose();
        }
    }

    /// <summary>What the opening bytes of a reply to an SD <c>GET</c> have shown so far.</summary>
    internal enum ReplyHeadKind
    {
        /// <summary>Not enough has arrived to tell.</summary>
        Undecided,

        /// <summary>The reply opens with something other than the no-card answer: file content.</summary>
        Content,

        /// <summary>The reply opens with the no-card line; an error line may still follow it.</summary>
        NoCardLine,

        /// <summary>
        /// The reply so far is the no-card line followed by the device's error line, with nothing
        /// after it but blank lines and the prompt. It is the answer once the reply goes quiet.
        /// </summary>
        NoCardAnswer,
    }

    /// <summary>
    /// Decides whether a reply opens with the device's "No SD Card Detected" answer (#1077).
    /// </summary>
    /// <remarks>
    /// Only the opening counts: blank lines, the prompt and the echo of this file's <c>GET</c> may
    /// come first, and the first other complete line has to be the answer. A log that quotes the
    /// same words further in is file content, so a text file whose first line is anything else is
    /// never mistaken for it. An unfinished last line that is still on course to be the echo counts
    /// as framing too, so a long file name arriving a byte at a time does not use up the answer's
    /// limit before its newline arrives.
    /// </remarks>
    /// <param name="head">The bytes the reply has opened with so far.</param>
    /// <param name="echo">The GET as it went out (see <see cref="ExpectedEcho"/>).</param>
    internal static (ReplyHeadKind Kind, int FramingBytes, int EchoBytes) ClassifyReplyHead(ReadOnlySpan<byte> head, string echo)
    {
        var text = Encoding.ASCII.GetString(head);
        var seenNoCardLine = false;
        var lineStart = 0;
        var framingBytes = 0;
        var echoBytes = 0;

        while (true)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                // An unfinished line is judged by what it can still become: the echo (framing),
                // the answer line, or after it the error line. One that can become none of them —
                // binary content, say — settles the opening as content now rather than at a
                // newline that may never come before the deadline.
                var partial = text.AsSpan(lineStart).Trim();
                if (seenNoCardLine)
                {
                    return (IsErrorLineSoFar(partial) ? ReplyHeadKind.NoCardLine : ReplyHeadKind.Content, framingBytes, echoBytes);
                }

                if (echoBytes == 0 && IsEchoSoFar(text.AsSpan(lineStart), echo))
                {
                    framingBytes = text.Length;
                    return (ReplyHeadKind.Undecided, framingBytes, echoBytes);
                }

                return (IsAnswerLineSoFar(partial) ? ReplyHeadKind.Undecided : ReplyHeadKind.Content, framingBytes, echoBytes);
            }

            var rawLine = text.AsSpan(lineStart, newline - lineStart);
            var line = rawLine.Trim();
            lineStart = newline + 1;

            // The prompt can open any line when echo is on; what follows it is the line.
            if (line.StartsWith(PromptPrefix, StringComparison.OrdinalIgnoreCase))
            {
                line = line[PromptPrefix.Length..].Trim();
            }

            if (seenNoCardLine)
            {
                if (line.IsEmpty)
                {
                    continue;
                }

                // The whole answer, so far: it is the device's only while nothing but blank lines
                // and the prompt follow it. A file can open with a copy of it (a saved transcript),
                // and then its next line settles that it is content.
                return (ScpiErrorLine.IsMatch(line.ToString()) && OnlyPromptsAndBlankLinesFrom(text, lineStart)
                    ? ReplyHeadKind.NoCardAnswer
                    : ReplyHeadKind.Content, framingBytes, echoBytes);
            }

            if (line.IsEmpty)
            {
                framingBytes = lineStart;
                continue;
            }

            // One GET, one echo: the first line that is exactly the bytes that went out, behind the
            // prompt. The same line again, or one that differs in case, spacing or line ending, is
            // the file's own.
            if (echoBytes == 0 && IsEchoLine(rawLine, echo))
            {
                framingBytes = lineStart;
                echoBytes = lineStart;
                continue;
            }

            if (line.Equals(NoSdCardAnswerLine, StringComparison.OrdinalIgnoreCase))
            {
                seenNoCardLine = true;
                continue;
            }

            return (ReplyHeadKind.Content, framingBytes, echoBytes);
        }
    }

    /// <summary>Whether everything in <paramref name="text"/> from <paramref name="start"/> is blank lines and prompts.</summary>
    private static bool OnlyPromptsAndBlankLinesFrom(string text, int start)
    {
        foreach (var line in text.AsSpan(start).EnumerateLines())
        {
            if (!WithoutPrompt(line.Trim()).IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a complete line (without its LF) is the echo: the command exactly as it went out,
    /// optionally behind the prompt, ending in the CR it was sent with.
    /// </summary>
    private static bool IsEchoLine(ReadOnlySpan<char> rawLine, string echo)
        => rawLine.EndsWith("\r", StringComparison.Ordinal)
            && WithoutEchoPrompt(rawLine[..^1]).SequenceEqual(echo);

    /// <summary>
    /// Whether an unfinished line can still become the echo: a start of the prompt, or the prompt
    /// (if any) followed by a start of the command exactly as it went out and its CR.
    /// </summary>
    private static bool IsEchoSoFar(ReadOnlySpan<char> partial, string echo)
    {
        if (PromptPrefix.AsSpan().StartsWith(partial, StringComparison.Ordinal))
        {
            return true;
        }

        var command = WithoutEchoPrompt(partial);
        return (echo + "\r").AsSpan().StartsWith(command, StringComparison.Ordinal);
    }

    /// <summary>The line with the prompt the firmware prints ahead of an echo, and the spaces after it, removed.</summary>
    private static ReadOnlySpan<char> WithoutEchoPrompt(ReadOnlySpan<char> line)
        => line.StartsWith(PromptPrefix, StringComparison.Ordinal)
            ? line[PromptPrefix.Length..].TrimStart(' ')
            : line;

    /// <summary>Whether an unfinished line can still become the no-card line, prompt or not.</summary>
    private static bool IsAnswerLineSoFar(ReadOnlySpan<char> partial)
        => NoSdCardAnswerLine.AsSpan().StartsWith(WithoutPrompt(partial), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The GET for <paramref name="fileName"/> as it reaches the device: the bytes
    /// <see cref="ScpiMessageProducer.GetSdFile"/> sends, decoded the way the reply is, so a name
    /// ASCII cannot carry ("café.bin") is matched as it is echoed ("caf?.bin").
    /// </summary>
    private static string ExpectedEcho(string fileName)
    {
        try
        {
            return Encoding.ASCII.GetString(ScpiMessageProducer.GetSdFile(fileName).GetBytes()).TrimEnd('\r', '\n');
        }
        catch (ArgumentException)
        {
            // A name the producer would not send: no GET for it went out, so there is no echo.
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether an unfinished line can still become a SCPI error line, prompt or not:
    /// <c>**ERROR:</c>, a numeric code, a comma and a quoted message, as <see cref="ScpiErrorLine"/>
    /// matches it once complete.
    /// </summary>
    private static bool IsErrorLineSoFar(ReadOnlySpan<char> partial)
    {
        const string ErrorLinePrefix = "**ERROR:";
        partial = WithoutPrompt(partial);
        if (partial.Length <= ErrorLinePrefix.Length)
        {
            return ErrorLinePrefix.AsSpan().StartsWith(partial, StringComparison.Ordinal);
        }

        if (!partial.StartsWith(ErrorLinePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = partial[ErrorLinePrefix.Length..].TrimStart();
        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest[0] == '-')
        {
            rest = rest[1..];
            if (rest.IsEmpty)
            {
                return true;
            }
        }

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        rest = rest[digits..].TrimStart();
        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest[0] != ',')
        {
            return false;
        }

        rest = rest[1..].TrimStart();

        // Once the message's opening quote has arrived, anything can follow it: the line still
        // ends in its closing quote.
        return rest.IsEmpty || rest[0] == '"';
    }

    /// <summary>
    /// The line with a leading prompt removed; a line that is still only part of the prompt is
    /// left empty, since it can become anything that follows one.
    /// </summary>
    private static ReadOnlySpan<char> WithoutPrompt(ReadOnlySpan<char> partial)
    {
        if (PromptPrefix.AsSpan().StartsWith(partial, StringComparison.OrdinalIgnoreCase))
        {
            return ReadOnlySpan<char>.Empty;
        }

        return partial.StartsWith(PromptPrefix, StringComparison.OrdinalIgnoreCase)
            ? partial[PromptPrefix.Length..].TrimStart()
            : partial;
    }

    private static async Task ReleaseHeldContentIfStillLiveAsync(
        HeldContentStream heldContent, CancellationToken transferToken, CancellationToken callerToken)
    {
        if (transferToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await heldContent.ReleaseHeldBytesAsync(transferToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // The deadline passed during the write; the transfer is reported for what ended it.
        }
    }

    private static SdCardNotPresentException NoCardAnswer(HeldContentStream heldContent, MemoryStream? replyHead)
    {
        heldContent.DiscardHeldBytes();

        var lines = replyHead is null
            ? Array.Empty<string>()
            : Encoding.ASCII.GetString(replyHead.GetBuffer(), 0, (int)replyHead.Length)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string? lastScpiError = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("**ERROR", StringComparison.Ordinal))
            {
                lastScpiError = line;
            }
        }

        return new SdCardNotPresentException(lines, lastScpiError);
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

    /// <summary>
    /// A write-through stream that holds the first bytes written to it in memory instead of
    /// passing them to the destination, until something settles whether they are file content at
    /// all. Used only for a transfer whose listing reports the file as empty.
    /// </summary>
    /// <remarks>
    /// The transfer loop writes content as it goes, which is right when the file is known to have
    /// content: it keeps a long download off the heap. A listed-empty file is the opposite case —
    /// the listing says there is no content, so the only bytes on the wire are the device's reply
    /// framing, and writing them would hand the caller a "file" made of the framing around an
    /// empty log. Holding them until a terminal marker claims them as content (a stale listing) or
    /// the device finishes without one (the listing was right) is what makes both answers correct.
    /// <para>
    /// Past <see cref="ListedEmptyFileReplyFramingLimitBytes"/> the listing is plainly stale, so the
    /// held bytes are released and every later write goes straight through. Nothing disposes the
    /// destination stream here; it belongs to the caller.
    /// </para>
    /// </remarks>
    private sealed class HeldContentStream : Stream
    {
        private readonly Stream _destination;
        private readonly int _framingLimitBytes;
        private MemoryStream? _heldBytes = new();

        public HeldContentStream(Stream destination, int framingLimitBytes)
        {
            _destination = destination;
            _framingLimitBytes = framingLimitBytes;
        }

        /// <summary>
        /// Whether any held byte has reached the destination stream. False means every byte
        /// written so far is still unclaimed reply framing that
        /// <see cref="DiscardHeldBytes"/> can still drop, so the transfer can still complete as
        /// the empty download its listing describes.
        /// </summary>
        public bool HasReleasedContent { get; private set; }

        /// <summary>
        /// While set, nothing is released however much is held: the limit is suspended until
        /// <see cref="StopHoldingAsync"/> or <see cref="ReleaseHeldBytesAsync"/>.
        /// </summary>
        public bool HoldUntilReleased { get; set; }

        /// <summary>Puts the ordinary limit back, releasing at once if it is already crossed.</summary>
        public async Task StopHoldingAsync(CancellationToken cancellationToken)
        {
            HoldUntilReleased = false;

            if (_heldBytes is { } held && held.Length > _framingLimitBytes)
            {
                await ReleaseHeldBytesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Hands every held byte to the destination stream and makes all later writes go straight
        /// through. Releasing nothing — the marker-only case — leaves
        /// <see cref="HasReleasedContent"/> false, because no content was ever claimed.
        /// </summary>
        public async Task ReleaseHeldBytesAsync(CancellationToken cancellationToken)
        {
            if (_heldBytes is not { } held)
            {
                return;
            }

            _heldBytes = null;

            if (held.Length > 0)
            {
                HasReleasedContent = true;
                await _destination.WriteAsync(held.GetBuffer(), 0, (int)held.Length, cancellationToken)
                    .ConfigureAwait(false);
            }

            held.Dispose();
        }

        /// <summary>
        /// Drops the held bytes without writing them. Called when the transfer ended with no
        /// terminator, which for a listed-empty file means those bytes were never file content.
        /// </summary>
        public void DiscardHeldBytes()
        {
            _heldBytes?.Dispose();
            _heldBytes = null;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_heldBytes is not { } held)
            {
                _destination.Write(buffer, offset, count);
                return;
            }

            held.Write(buffer, offset, count);

            if (HoldUntilReleased || held.Length <= _framingLimitBytes)
            {
                return;
            }

            // The synchronous twin of ReleaseHeldBytesAsync, rather than a blocking wait on it:
            // the receiver only ever writes asynchronously, so this path exists to keep the
            // Stream contract honest, not to be fast.
            _heldBytes = null;
            HasReleasedContent = true;
            _destination.Write(held.GetBuffer(), 0, (int)held.Length);
            held.Dispose();
        }

        public override async Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_heldBytes is not { } held)
            {
                await _destination.WriteAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            held.Write(buffer, offset, count);

            if (!HoldUntilReleased && held.Length > _framingLimitBytes)
            {
                await ReleaseHeldBytesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Flush() => _destination.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Only the in-memory buffer. The destination stream is the caller's.
                _heldBytes?.Dispose();
                _heldBytes = null;
            }

            base.Dispose(disposing);
        }
    }
}
