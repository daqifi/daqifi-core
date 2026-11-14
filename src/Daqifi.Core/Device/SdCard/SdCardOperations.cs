using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// The <see cref="ISdCardOperations"/> implementation, extracted from
/// <see cref="DaqifiStreamingDevice"/> so the device delegates rather than hosts it. Also owns
/// the shared-SPI-bus handover (<see cref="PrepareSdInterface"/> /
/// <see cref="PrepareLanInterface"/>) that <see cref="Network.INetworkConfigurable"/> exposes,
/// because every operation here depends on it.
/// </summary>
/// <remarks>
/// Holds the SD-scoped device state — whether a logging session is running, the most recent
/// directory listing, and the single-download gate — so that state lives next to the only code
/// that reads it. Everything that touches the wire goes through
/// <see cref="ISdCardOperationHost"/>, which keeps the device's virtual members (and therefore
/// any subclass override of them) in the path.
/// </remarks>
internal sealed class SdCardOperations
{
    /// <summary>
    /// Spacing between the SCPI commands that start an SD logging session. Was an unnamed
    /// <c>100</c> repeated six times; named because these are now driven by
    /// <see cref="ISdCardOperationHost.TimeProvider"/> (issue #637). The duration is unchanged,
    /// and it is deliberately its own constant rather than a reuse of
    /// <see cref="ISdCardOperationHost.SdInterfaceSettleDelay"/>: that one is the SPI-bus
    /// interface switch settling, this one is command spacing, and they happen to agree today.
    /// </summary>
    private static readonly TimeSpan SdLogStartCommandSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum number of retry attempts for SD card list operations that receive transient
    /// SCPI errors (e.g., -200 Execution error) due to interface-switch timing.
    /// </summary>
    private const int SD_LIST_MAX_RETRIES = 1;

    /// <summary>
    /// How many times a listing or storage query asks again after the device answered
    /// "No SD Card Detected", before reporting the card absent (#896). Recheck <c>n</c> waits
    /// <c>n</c> × <see cref="ISdCardOperationHost.SdNoCardRecheckDelay"/>, so the three cover
    /// 1 + 2 + 3 = 6 s.
    /// </summary>
    /// <remarks>
    /// The firmware answers presence from its last card-detect poll. That poll runs once a
    /// second, and once every 5 s after ten polls in a row see no card. Firmware v3.9.0 starts a
    /// fresh poll as it answers, so the first recheck sees the real state. Older supported
    /// firmware does not: its answer can stay stale until the next scheduled poll, up to 5 s
    /// later. Six seconds covers that and the card's start-up. A card that is really absent is
    /// reported about 6 s later, on an SD call only.
    /// </remarks>
    private const int SD_NO_CARD_MAX_RECHECKS = 3;

    /// <summary>
    /// SCPI error the device queues when <c>SD:GET</c> names a file or directory that is not on
    /// the card (firmware v3.9.0+, <c>SD_ASYNC_ERR_FILE_NOT_FOUND</c>).
    /// </summary>
    private const int ScpiFileNameNotFound = -256;

    /// <summary>
    /// SCPI error the device queues when it rejects the name <c>SD:GET</c> asked for (firmware
    /// v3.9.0+, <c>SD_ASYNC_ERR_FILE_NAME_ERROR</c>).
    /// </summary>
    private const int ScpiFileNameError = -257;

    /// <summary>
    /// SCPI error the device queues when <c>SD:GET</c> cannot open its file because the storage
    /// is unusable: no card mounted, access denied, out of file handles (firmware v3.9.0+,
    /// <c>SD_ASYNC_ERR_MASS_STORAGE</c>).
    /// </summary>
    private const int ScpiMassStorageError = -250;

    /// <summary>
    /// Most <c>SYSTem:ERRor?</c> reads spent draining the queue before or after an <c>SD:GET</c>.
    /// The firmware's queue holds 17 entries (<c>scpi_error_queue_data[17]</c>), so 18 reads
    /// always reach "No error" on a full queue.
    /// </summary>
    private const int SdGetErrorQueueDrainCap = 18;

    /// <summary>
    /// Firmware status text for a missing card. Not SCPI-shaped, so
    /// <see cref="ScpiResponseClassifier.IsErrorResponseLine"/> is what classifies it and this
    /// is the substring both <see cref="GetSdCardListError"/> and
    /// <see cref="ExplainsItsOwnMissingEndMarker"/> look for — single-sourced so the two cannot
    /// disagree about which replies already account for themselves.
    /// </summary>
    private const string NoSdCardMarker = "No SD Card Detected";

    /// <summary>
    /// Firmware status text for a directory the walk could not open. Single-sourced for the
    /// same reason as <see cref="NoSdCardMarker"/>; note the line carries a bracketed
    /// <c>[Error:n]</c> prefix rather than an <c>ERROR</c> token, so the SCPI classifiers do
    /// not match it and the substring is the only handle on it.
    /// </summary>
    private const string DirectoryOpenFailureMarker = "Failed to open directory";

    /// <summary>
    /// Inactivity window that ends the SD listing text exchange, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Deliberately longer than the 250ms default. The listing is only accepted once its
    /// end-of-listing terminator has been seen (see <see cref="GetSdCardFilesAsync"/>), and the
    /// terminator can trail the last listing line by more than the default window — the firmware
    /// walks the directory tree between chunks, and a congested WiFi link adds its own gaps. With
    /// the default, a merely-slow terminator would read as a missing one and fail a listing that
    /// was about to complete.
    /// </remarks>
    private const int SD_LIST_COMPLETION_TIMEOUT_MS = 1000;

    /// <summary>
    /// libscpi's <c>SCPI_ERROR_UNDEFINED_HEADER</c> — the code the firmware returns for a
    /// command it doesn't recognize (e.g. a command that postdates the connected firmware).
    /// This is the wire-level signal behind the <see cref="FeatureNotSupportedException"/>
    /// backstop (ADR 0001, docs/adr/0001-firmware-feature-gating.md).
    /// </summary>
    private const int ScpiErrorCodeUndefinedHeader = -113;

    /// <summary>libscpi's <c>SCPI_ERROR_EXECUTION_ERROR</c>.</summary>
    private const int ScpiErrorCodeExecutionError = -200;

    /// <summary>libscpi's <c>SCPI_ERROR_SETTINGS_CONFLICT</c>.</summary>
    private const int ScpiErrorCodeSettingsConflict = -221;

    /// <summary>
    /// How long <c>SYSTem:STORage:SD:PROTect</c> may take to answer, in milliseconds. The
    /// firmware holds its reply for up to two seconds (<c>SCPI_SD_PROTECT_APPLY_MS</c>) while it
    /// waits for the SD driver to drop the card for re-initialization, and only then reports a
    /// change it could not apply; the rest is margin for a WiFi link.
    /// </summary>
    private const int SD_PROTECT_RESPONSE_TIMEOUT_MS = 5000;

    /// <summary>
    /// How long <see cref="SetSdCardCrcProtectionAsync"/> keeps trying to mount the card after the
    /// device has accepted a change, before it reports <see cref="SdCardCrcProtectionResult.Pending"/>.
    /// </summary>
    /// <remarks>
    /// The device applies a change by dropping the card, and picks it up again at its next
    /// card-detect poll, about a second later (measured on firmware v3.9.0: unmountable for
    /// 0.5–1.0 s). Several polls' worth, so one slow init does not read as a card that never
    /// came back.
    /// </remarks>
    private static readonly TimeSpan SdProtectCardReadyTimeout = TimeSpan.FromSeconds(4);

    private readonly ISdCardOperationHost _host;

    private IReadOnlyList<SdCardFileInfo> _sdCardFiles = Array.Empty<SdCardFileInfo>();

    /// <summary>
    /// Admits one SD download at a time. A download that hits its deadline is ABANDONED, not
    /// stopped — its worker can still be parked in native I/O holding the transport stream —
    /// so the gate is released only when that worker actually finishes, however long that
    /// takes. Without it, a caller retrying against a device that stays wedged (an "import
    /// all" loop, say) would start a second reader on the same stream, which is the framing
    /// corruption <see cref="DaqifiDevice"/> already refuses to risk when restarting its
    /// reader, and would stack another permanently blocked thread each time (#399).
    /// </summary>
    /// <remarks>
    /// Deliberately not disposed: we only ever call <see cref="SemaphoreSlim.Wait(int)"/> and
    /// <see cref="SemaphoreSlim.Release()"/>, never <see cref="SemaphoreSlim.AvailableWaitHandle"/>,
    /// so there is no handle to release — and an abandoned worker may release this long after
    /// the device is disposed, which would otherwise fault a continuation nobody observes.
    /// </remarks>
    private readonly SemaphoreSlim _sdDownloadGate = new(1, 1);

    /// <summary>
    /// Runs one <see cref="SetSdCardCrcProtectionAsync"/> at a time, from the set to the end of
    /// its confirmation. The device keeps one setting, so an opposite call that got in while this
    /// one waited for the card would leave the card in the other mode, and this call's mount
    /// would look like its own change arriving.
    /// </summary>
    /// <remarks>
    /// An SD log start takes it too, around its claim on the bus, so a start can never land
    /// between a change's log check and its defensive stream stop, which would end the new log.
    /// A start that arrives during a change waits for the change to be confirmed.
    /// Not disposed, for the reason <see cref="_sdDownloadGate"/> is not.
    /// </remarks>
    private readonly SemaphoreSlim _sdProtectGate = new(1, 1);

    /// <summary>
    /// Who has the SPI bus that the LAN module and the SD card share, as far as the commands
    /// this class has sent can tell (issue #941).
    /// </summary>
    private enum SdBusMode
    {
        /// <summary>The default: LAN on, SD storage off, stream routed to USB.</summary>
        LanActive,

        /// <summary>A file operation (list, delete, download, format, space query) has the card.</summary>
        SdBorrowed,

        /// <summary>The start sequence of an SD log is going out; the device is part-way onto the card.</summary>
        LogStarting,

        /// <summary>An SD log is recording.</summary>
        Logging,

        /// <summary>The stop sequence of an SD log is going out.</summary>
        LogEnding,
    }

    /// <summary>
    /// The one record of the bus mode, a <see cref="SdBusMode"/> held as an <see cref="int"/>
    /// so it can be swapped atomically. Before this the mode existed only as the order of the
    /// <c>Send</c> calls, so a start sequence that was part-way through, or had stopped
    /// part-way, looked exactly like an idle device (issue #941).
    /// </summary>
    /// <remarks>
    /// Written only by the transition methods: <see cref="PrepareSdInterface"/>,
    /// <see cref="PrepareLanInterface"/>, <see cref="ClaimBusForLogStart"/>,
    /// <see cref="StartSdCardLoggingSessionAsync"/>, <see cref="EndLoggingSession"/>,
    /// <see cref="FormatSdCardAsync"/> and <see cref="IsBusHeldByLog"/>.
    /// <para>
    /// Remaining limit: the file operations check this at their guard and only record
    /// <see cref="SdBusMode.SdBorrowed"/> when their bus switch goes out, so a log start that
    /// arrives between the two is still admitted. And <see cref="SdBusMode.SdBorrowed"/> is
    /// not something a start can be refused on: a borrow is not always handed back (a device
    /// that drops mid-operation, an abandoned download, a caller's own
    /// <see cref="PrepareSdInterface"/>, a format), so refusing on it could refuse logging for
    /// the rest of the connection.
    /// </para>
    /// </remarks>
    private int _busMode = (int)SdBusMode.LanActive;

    private SdBusMode BusMode => (SdBusMode)Volatile.Read(ref _busMode);

    private void EnterBusMode(SdBusMode mode) => Volatile.Write(ref _busMode, (int)mode);

    /// <summary>
    /// Moves the bus mode to <paramref name="to"/> only if it is <paramref name="from"/>, so a
    /// borrow or a restore never overwrites the record of a log that owns the bus.
    /// </summary>
    private bool MoveBusMode(SdBusMode from, SdBusMode to) =>
        Interlocked.CompareExchange(ref _busMode, (int)to, (int)from) == (int)from;

    /// <summary>
    /// Takes the bus for a log start, or refuses when a log already has it. The start goes
    /// through the same gate it puts in front of every other SD operation: without this a
    /// second start was admitted during the first, and if it was then cancelled or failed, its
    /// cleanup sent the stop sequence and ended the first caller's recording (issue #941).
    /// </summary>
    /// <exception cref="SdCardBusyException">A log is starting, recording or stopping.</exception>
    private void ClaimBusForLogStart()
    {
        while (true)
        {
            if (IsBusHeldByLog())
            {
                throw new SdCardBusyException(Array.Empty<string>());
            }

            // Compare-exchange from the mode just read, so two starts racing here cannot both
            // win: the loser re-reads, finds LogStarting and is refused.
            var current = BusMode;
            if (current is SdBusMode.LanActive or SdBusMode.SdBorrowed
                && MoveBusMode(current, SdBusMode.LogStarting))
            {
                return;
            }
        }
    }

    /// <summary>
    /// True while an SD log owns the bus: starting, recording or stopping. Every other SD
    /// operation refuses in that case, because each of them ends by switching SD storage off
    /// and the LAN back on — which during the start sequence left a "successful" log with
    /// nowhere to write (issue #941), and during a recording ended it (issue #851).
    /// </summary>
    /// <remarks>
    /// A recording can end without this class: the device's own stop on re-initialization, a
    /// raw <c>StopStreamData</c>, a disconnect. Those end the stream session, so a
    /// <see cref="SdBusMode.Logging"/> record with no session behind it is released here
    /// rather than left to refuse SD operations forever.
    /// </remarks>
    private bool IsBusHeldByLog()
    {
        if (!_host.IsLoggingToSdCard)
        {
            MoveBusMode(SdBusMode.Logging, SdBusMode.LanActive);
        }

        return BusMode is SdBusMode.LogStarting or SdBusMode.Logging or SdBusMode.LogEnding;
    }

    internal SdCardOperations(ISdCardOperationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// The clock every settle delay, budget and deadline here is measured on (issue #637).
    /// </summary>
    /// <remarks>
    /// Read through the host on each access rather than captured in the constructor, for the
    /// same reason the two SD timeouts are: the device owns it, and a test that swaps it must
    /// reach a collaborator that was built during the device's own construction.
    /// </remarks>
    private TimeProvider Clock => _host.TimeProvider;

    /// <summary>
    /// How long to wait after switching the shared SPI bus between the LAN and the SD card.
    /// The SD card and LAN share that bus, so the firmware needs a settle period to complete
    /// the interface switch before it will answer further commands.
    /// </summary>
    /// <remarks>
    /// Read through the host on each access, for the same reason <see cref="Clock"/> and the two
    /// SD timeouts are: the device owns it, and a test that shortens it must reach a collaborator
    /// that was built during the device's own construction.
    /// </remarks>
    private TimeSpan SdInterfaceSettleDelay => _host.SdInterfaceSettleDelay;

    /// <inheritdoc cref="ISdCardOperationHost.SdNoCardRecheckDelay"/>
    private TimeSpan SdNoCardRecheckDelay => _host.SdNoCardRecheckDelay;

    /// <inheritdoc cref="ISdCardOperations.SdCardFiles" />
    internal IReadOnlyList<SdCardFileInfo> SdCardFiles => _sdCardFiles;

    /// <summary>
    /// Prepares the SD-card interface for a file operation. Over USB the LAN interface is
    /// disabled first to free the shared SPI bus for the SD card. Over WiFi/TCP (firmware
    /// &gt;= v3.7.0, #598/#599) the LAN interface MUST stay enabled — the Harmony SPI driver
    /// arbitrates SD/WiFi transactions on the shared bus, and the SD reply routes back over the
    /// very TCP channel that requested it, so disabling LAN would drop the control channel
    /// mid-operation. Only the SD subsystem is enabled in that case.
    /// </summary>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    internal void PrepareSdInterface()
    {
        _host.EnsureConnected();

        if (_host.IsUsbConnection)
        {
            _host.Send(ScpiMessageProducer.DisableNetworkLan);
        }

        _host.Send(ScpiMessageProducer.EnableStorageSd);

        MoveBusMode(SdBusMode.LanActive, SdBusMode.SdBorrowed);
    }

    /// <summary>
    /// Restores the interface after an SD-card file operation. The SD subsystem is disabled in
    /// both cases. Over USB the LAN interface is re-enabled (it was disabled by
    /// <see cref="PrepareSdInterface"/>). Over WiFi/TCP the LAN was never disabled, so it is
    /// left alone — re-enabling it would re-initialize the WiFi module and drop the connection.
    /// </summary>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    internal void PrepareLanInterface()
    {
        _host.EnsureConnected();

        _host.Send(ScpiMessageProducer.DisableStorageSd);

        if (_host.IsUsbConnection)
        {
            _host.Send(ScpiMessageProducer.EnableNetworkLan);
        }

        MoveBusMode(SdBusMode.SdBorrowed, SdBusMode.LanActive);
    }

    /// <summary>
    /// Applies the transport predicate for an SD-card operation that drives the card while the
    /// link is active (LIST / GET / DELETE and the storage-space query). Over USB (serial) these
    /// are available on all SD-capable firmware and are not gated. Over WiFi/TCP they are gated
    /// on <see cref="DeviceFeature.SdFileTransferOverWifi"/>, which
    /// <see cref="DaqifiDevice.EnsureSupported"/> resolves against the requirement table
    /// (ADR 0001) — pre-empting a command the firmware cannot service over WiFi, which would
    /// otherwise stall on the shared SPI bus.
    /// </summary>
    /// <remarks>
    /// This is only the transport half of the gate: which feature applies depends on the active
    /// transport, but whether the device has that feature is the seam's answer, not this
    /// method's.
    /// </remarks>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the active transport is not USB and the device does not support
    /// <see cref="DeviceFeature.SdFileTransferOverWifi"/>.
    /// </exception>
    private void EnsureSdFileTransferSupportedOnTransport()
    {
        if (_host.IsUsbConnection)
        {
            return;
        }

        _host.EnsureSupported(DeviceFeature.SdFileTransferOverWifi);
    }

    /// <summary>
    /// Retrieves the list of files stored on the device's SD card.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation, containing the list of files.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
    /// <exception cref="SdCardFilesystemException">Thrown when the SD card filesystem cannot satisfy the request (corrupt card, unreadable directory).</exception>
    /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error that did not match a more specific condition. Empty directories return an empty list rather than throwing.</exception>
    /// <exception cref="SdCardListIncompleteException">
    /// Thrown when the listing cannot be trusted as complete — the <c>SYSTem:ERRor?</c> terminator
    /// never came back, or the device's <c>__END_OF_LIST__</c> marker reported the walk as
    /// incomplete or failed. Distinguishing this from a genuinely empty card is the whole point
    /// of the checks described in the remarks (closes #396).
    /// </exception>
    /// <remarks>
    /// <para>
    /// An empty directory produces no file lines. Firmware before #794 sends nothing else, so a lost
    /// or truncated reply is byte-for-byte indistinguishable from a healthy empty card; current
    /// firmware adds an end-of-listing marker, but a missing marker is also what a truncated reply
    /// looks like, so the marker alone cannot prove the device answered. Core closes that gap by
    /// appending a <c>SYSTem:ERRor?</c> query to the same text exchange: the transport delivers in
    /// order and the firmware does not process the next command until the listing has been handed
    /// to the output, so receiving the reply proves both that the device is answering and that the
    /// listing ahead of it is complete. Its absence means the response is incomplete, and the
    /// caller gets an exception instead of a plausible-looking empty list.
    /// </para>
    /// <para>
    /// The terminator is only meaningful if it cannot be confused with a late reply to an
    /// earlier command, so two things guard that boundary: the text exchange discards whatever
    /// was already in flight when it opened, and this method's SPI-bus switch and settle delay
    /// run as the exchange's prepare phase, ahead of that boundary, leaving the exchange with no
    /// internal gap for a stale reply to slip into.
    /// </para>
    /// <para>
    /// The terminator's error code is used only as a liveness marker, never for classification:
    /// the queue it pops can hold entries left by earlier commands, so attributing the code to
    /// this listing would misreport stale failures. SD errors continue to be classified from the
    /// listing lines themselves. Note the side effect this implies — each listing consumes one
    /// entry from the device's SCPI error queue, so a
    /// <see cref="DaqifiDevice.DrainErrorQueueAsync"/> run afterwards will not see the entry
    /// this listing generated.
    /// </para>
    /// <para>
    /// A reply the terminator closed is then read for the device's own end-of-listing marker
    /// (<c>__END_OF_LIST__</c>, firmware #794). <c>OK</c> is a finished walk. <c>FAILED</c>,
    /// <c>INCOMPLETE</c>, and any other status word —
    /// <see cref="SdCardFileListParser.GetListingStatus"/> treats an unrecognized word as
    /// incomplete — throw <see cref="SdCardListIncompleteException"/> for the same reason a
    /// missing terminator does: the list must not be cached as the answer to whether a file is
    /// on the card.
    /// </para>
    /// <para>
    /// No marker at all is read against <see cref="DeviceFeature.SdListingEndMarker"/>. Below
    /// firmware v3.8.0 the device never emits one, so its absence says nothing and is not an
    /// error — the terminator already showed the exchange completed. At v3.8.0 and above the
    /// device does emit one, in the same reply and ahead of the terminator, so a reply that
    /// closed without a marker is a walk that never reached its own end: the listing path
    /// retries it once and then raises <see cref="SdCardListIncompleteException"/> rather than
    /// hand back the entries it did get as the card's contents. A reply that explains its own
    /// missing marker — no card, an unreadable directory, any firmware status text — keeps its
    /// more specific answer from <see cref="GetSdCardListError"/> instead, and is not
    /// retried as incomplete (closes #887).
    /// </para>
    /// <para>
    /// A "No SD Card Detected" answer is asked again up to three times, over about 6 s, before
    /// <see cref="SdCardNotPresentException"/> is thrown. The firmware answers it from its last
    /// card-detect poll, which can be up to 5 s out of date on firmware before v3.9.0; the first
    /// query of a session would otherwise report a card that is there as missing (#896).
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        // Refuse while a logging session is running, the same as the sibling operations above and
        // below (storage query, delete, both downloads). Without this the listing ended the
        // recording: it stops the stream, and its LAN restore sends SD:ENAble 0, which the
        // firmware treats as a release of the card from streaming
        // (SCPIStorageSD.c, Streaming_SdInterfaceReleased). The stream was then resumed, but no
        // longer to the card, while IsLoggingToSdCard still reported true — a call that reads
        // like a query silently threw away the rest of the capture (closes #851).
        //
        // Refusing is the only side-effect-free answer. Skipping the SD:ENAble pair does not make
        // a listing safe: both branches of the firmware's SD:ENAble handler set the SD manager
        // mode to NONE, which tears down a write session, and SD:LIST? is answered with -200
        // while the manager is busy. So there is no command order that lists during a session
        // without ending it.
        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        EnsureSdFileTransferSupportedOnTransport();

        cancellationToken.ThrowIfCancellationRequested();

        var wasStreaming = PauseStreamingForSdOperation();

        try
        {
            SdListVerdict verdict = default;
            var transientRetries = 0;
            var noCardRechecks = 0;
            TimeSpan? delayBeforeNextAttempt = null;

            // A SCPI error here is often a transient timing issue, and an unterminated response
            // can be a one-off stall, so both are retried SD_LIST_MAX_RETRIES times after an
            // additional settle delay before being surfaced. A "no card" answer has its own,
            // longer schedule (SD_NO_CARD_MAX_RECHECKS): it can be the firmware's stale
            // card-detect poll, which a 100 ms retry is too soon to outlast (#896).
            while (true)
            {
                if (delayBeforeNextAttempt is { } delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false);
                }

                // The SPI bus switch and its settle wait run as the exchange's prepare phase, and
                // the restore as its finalize phase: both inside the exchange lock, so a competing
                // text exchange can neither restore the LAN interface between the switch and the
                // LIST nor slip in between the LIST and the restore. The prepare also sits ahead of
                // the stale-line boundary, so its settle wait does not become a window in which a
                // late reply to an earlier command could pass for this listing's terminator.
                // Querying the card too soon after the switch makes the device answer -200
                // (Execution error), so the wait itself is not optional.
                //
                // Each attempt therefore leaves the bus back on LAN, including across the retry
                // delay above — the pairing is per exchange rather than per call so that gap, which
                // is outside the lock, is not one in which the device sits switched to the card.
                var lines = await _host.ExecuteTextCommandAsync(
                    () =>
                    {
                        _host.Send(ScpiMessageProducer.GetSdFileList);

                        // End-of-listing terminator — see this method's remarks. Sent inside
                        // the same text exchange so the ordering guarantee holds.
                        _host.Send(ScpiMessageProducer.GetSystemError);
                    },
                    responseTimeoutMs: 3000,
                    completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                    cancellationToken: cancellationToken,
                    prepareAsync: PrepareSdInterfaceAndSettleAsync,
                    finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

                // One verdict for the reply, from the same method the delete path's relist
                // uses, so the two cannot disagree about what a good listing is (#942). An
                // unexplained missing marker is read again for the same reason a SCPI error is:
                // on firmware that emits one it means the directory walk did not finish, and a
                // walk cut short by the shared SPI bus being briefly unavailable is exactly the
                // transient this loop exists for.
                verdict = ClassifySdListReply(lines);
                if (verdict.Kind == SdListVerdictKind.NoCard)
                {
                    if (noCardRechecks++ < SD_NO_CARD_MAX_RECHECKS)
                    {
                        delayBeforeNextAttempt = SdNoCardRecheckDelay * noCardRechecks;
                        continue;
                    }

                    break;
                }

                if (!verdict.ShouldReadAgain || transientRetries++ >= SD_LIST_MAX_RETRIES)
                {
                    break;
                }

                delayBeforeNextAttempt = SdInterfaceSettleDelay;
            }

            if (verdict.Kind == SdListVerdictKind.DirectoryNotFound)
            {
                // A log directory that is not on the card is an empty card, and that is
                // what this method reports: no files. A new card has no log directory
                // until the first capture writes one, so the old exception made the
                // normal state of a new card look like a fault, and a caller had to
                // catch to learn there was nothing to list (closes #730).
                //
                // Here rather than in ClassifySdListReply, because the verdict has a
                // second caller: DeleteSdCardFileAsync relists in the delete's own
                // exchange, and there the missing directory leaves "is the file I named
                // gone?" unanswered. That caller still needs the exception, so the
                // verdict carries it for both and the read path decides for itself.
                //
                // The verdict already ranks the conditions, so a reply that says both "no
                // SD card" and "no directory" is NoCard and still raises
                // SdCardNotPresentException.
                _sdCardFiles = Array.Empty<SdCardFileInfo>();
                return _sdCardFiles;
            }

            if (!verdict.IsUsable)
            {
                throw verdict.Error!;
            }

            var files = SdCardFileListParser.ParseFileList(verdict.Listing);
            _sdCardFiles = files;
            return files;
        }
        finally
        {
            RestoreStreamingIfNeeded(wasStreaming);
        }
    }

    /// <summary>
    /// Prepare phase shared by the SD card text exchanges: switches the shared SPI bus over to
    /// the card and waits for the firmware to complete the switch.
    /// </summary>
    /// <remarks>
    /// Passed as the <c>prepareAsync</c> phase of
    /// <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>
    /// rather than run
    /// inline, so it executes inside the text-exchange lock — a competing exchange restoring the
    /// LAN interface between the switch and the commands that depend on it would leave them
    /// running against the wrong interface — and ahead of the exchange's stale-line boundary, so
    /// the settle wait cannot be mistaken for a window in which the device was answering.
    /// </remarks>
    private async Task PrepareSdInterfaceAndSettleAsync(CancellationToken cancellationToken)
    {
        PrepareSdInterface();

        // Querying the card too soon after the switch makes the device answer -200
        // (Execution error), so this wait is not optional.
        await Task.Delay(SdInterfaceSettleDelay, Clock, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalize phase shared by the SD card text exchanges: hands the shared SPI bus back to the
    /// LAN interface. The mirror of <see cref="PrepareSdInterfaceAndSettleAsync"/>.
    /// </summary>
    /// <remarks>
    /// Passed as the <c>finalizeAsync</c> phase of
    /// <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>
    /// rather than run from the caller's own <c>finally</c>, so it holds the same lock
    /// acquisition the matching prepare phase does. Restoring from outside the lock leaves a
    /// window in which a competing exchange runs between this operation's commands and its
    /// restore — the switch serialized, the restore not (#407).
    /// <para>
    /// The connection check keeps a restore off a device that dropped mid-operation, where the
    /// sends would only throw <see cref="DeviceNotConnectedException"/> over the top of whatever
    /// actually failed. Nothing to restore in that case: the link is gone.
    /// </para>
    /// </remarks>
    private Task RestoreLanInterfaceAsync()
    {
        if (_host.IsConnected)
        {
            PrepareLanInterface();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Silences streaming ahead of an SD-card operation and reports whether a live stream was
    /// actually running, so <see cref="RestoreStreamingIfNeeded"/> can put it back afterwards
    /// (#533). The stop half is unconditional even when the flag says nothing is streaming,
    /// because the flag can be stale (issue #118).
    /// </summary>
    /// <returns>
    /// <see cref="IDeviceOperationHost.IsStreaming"/> as it stood immediately before the stop.
    /// Pass it to <see cref="RestoreStreamingIfNeeded"/> from the operation's <c>finally</c>,
    /// which uses it to tell "this operation silenced a live stream" apart from "nothing was
    /// streaming to begin with".
    /// </returns>
    /// <remarks>
    /// The capture has to come before the stop with nothing in between: read it afterwards and
    /// it reports state this method has already cleared, so the stream is never resumed. Owning
    /// both halves here is what holds that ordering at every call site, rather than each one
    /// re-deriving it.
    /// </remarks>
    private bool PauseStreamingForSdOperation()
    {
        var wasStreaming = _host.IsStreaming;

        // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
        _host.Send(ScpiMessageProducer.StopStreaming);
        _host.EndStreamingSession();

        return wasStreaming;
    }

    /// <summary>
    /// Resumes streaming after an SD-card operation's defensive stop, if a live stream was
    /// actually running when the operation started (#533). Every SD operation stops streaming
    /// unconditionally before it touches the card, whether or not anyone was consuming it; left
    /// unrestored, a caller reading <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/>
    /// concurrently goes silent for the rest of the read with no error, because the device is
    /// still connected — only no longer streaming.
    /// </summary>
    /// <param name="wasStreaming">
    /// Whether <see cref="IDeviceOperationHost.IsStreaming"/> was <c>true</c> immediately before
    /// this operation's defensive stop. Restoring unconditionally would start streaming on a
    /// device nothing had asked to stream from.
    /// </param>
    /// <remarks>
    /// Goes through <see cref="IDeviceOperationHost.StartStreaming"/> rather than a raw
    /// <c>Send(ScpiMessageProducer.StartStreaming(...))</c>, so the resumed stream gets the same
    /// per-session frame-decoder reset (timestamp anchor, gap detector) any other
    /// <see cref="DaqifiStreamingDevice.StartStreaming"/> call gets, instead of reusing the
    /// session that was running before the SD operation.
    /// </remarks>
    private void RestoreStreamingIfNeeded(bool wasStreaming)
    {
        if (!wasStreaming || !_host.IsConnected)
        {
            return;
        }

        try
        {
            _host.StartStreaming();
        }
        catch
        {
            // Best-effort, the same as the LAN restore this mirrors (RestoreLanInterfaceAsync,
            // DownloadSdCardFileAsync's finally): called from a finally block, so letting a
            // failure here escape would replace whatever the SD operation itself threw — or
            // turn a clean success into a failure — over a device that may simply have dropped
            // between the IsConnected check above and this call.
            //
            // DaqifiStreamingDevice.StartStreaming sets IsStreaming = true BEFORE it sends the
            // SCPI command, so a Send failure here can leave the flag true for a stream that
            // never actually resumed. Left alone, that is exactly the "flag says streaming, the
            // device isn't" staleness #533 exists to fix — just relocated to this catch instead
            // of fixed by it. Correcting it here is safe: this catch is the only code that knows
            // the resume attempt failed.
            _host.MarkStreamingStartRefused();
        }
    }

    /// <summary>
    /// Splits a raw SD listing response at the <c>SYSTem:ERRor?</c> terminator reply that
    /// <see cref="GetSdCardFilesAsync"/> appends to the exchange.
    /// </summary>
    /// <param name="lines">The raw response lines captured from the device.</param>
    /// <param name="listingLines">
    /// The lines that precede the terminator — the directory listing proper — when the method
    /// returns <c>true</c>; otherwise the unmodified input.
    /// </param>
    /// <returns>
    /// <c>true</c> when the terminator was present, meaning the response is complete;
    /// <c>false</c> when it never arrived, meaning the response is missing or truncated.
    /// </returns>
    private static bool TrySplitAtSdListTerminator(
        IReadOnlyList<string> lines,
        out IReadOnlyList<string> listingLines)
    {
        // Scan from the end. A terminator reply from a PREVIOUS, timed-out exchange can still
        // be sitting in the transport buffer and lead this response; splitting at the first
        // match would then discard the listing that follows it and report an empty card —
        // exactly the failure this terminator exists to prevent.
        var terminatorIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (ScpiResponseClassifier.IsSystemErrorReplyLine(lines[i]))
            {
                terminatorIndex = i;
                break;
            }
        }

        if (terminatorIndex < 0)
        {
            listingLines = lines;
            return false;
        }

        var listing = new List<string>(terminatorIndex);
        for (var j = 0; j < terminatorIndex; j++)
        {
            // Any other terminator-shaped line is a stale reply of the same kind, not
            // directory content — no firmware listing entry can match that shape, since
            // entries are always "<path> <size>".
            if (ScpiResponseClassifier.IsSystemErrorReplyLine(lines[j]))
            {
                continue;
            }

            listing.Add(lines[j]);
        }

        listingLines = listing;
        return true;
    }

    /// <summary>
    /// True when a listing carries no end-of-listing marker on firmware that emits one, and
    /// nothing else in the reply accounts for its absence — the shape that
    /// <see cref="GetSdCardFilesAsync"/> must not report as the card's contents (closes #887).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, each load-bearing. <see cref="SdCardListingStatus.Unterminated"/> is
    /// the only status the device cannot have told us anything with. The
    /// <see cref="DeviceFeature.SdListingEndMarker"/> gate is what makes that silence mean
    /// something: below firmware v3.8.0 a listing simply stops, so every reply on those
    /// versions is unterminated and an empty card would otherwise start throwing. And a reply
    /// that already explains itself — <c>Error !! No SD Card Detected</c>, a filesystem error,
    /// any other firmware status text — is left to <see cref="GetSdCardListError"/>, which
    /// has a more specific answer than "incomplete" and reaches it without a retry.
    /// </para>
    /// <para>
    /// Read through <see cref="IDeviceOperationHost.Supports"/> on each call rather than cached,
    /// for the reason ADR 0001 gives: the firmware version arrives with the device's metadata
    /// and a collaborator built during construction would have cached the answer from before it
    /// did.
    /// </para>
    /// </remarks>
    /// <param name="listing">The listing lines, already split from the transport terminator.</param>
    /// <param name="status">The listing's status per <see cref="SdCardFileListParser.GetListingStatus"/>.</param>
    private bool IsUnexplainedMissingListingEndMarker(
        IReadOnlyList<string> listing,
        SdCardListingStatus status)
    {
        if (status != SdCardListingStatus.Unterminated)
        {
            return false;
        }

        if (!_host.Supports(DeviceFeature.SdListingEndMarker))
        {
            return false;
        }

        return !ExplainsItsOwnMissingEndMarker(listing);
    }

    /// <summary>
    /// True when a reply already says why it carries no end-of-listing marker, so
    /// <see cref="GetSdCardListError"/> has a more specific answer than "incomplete" and
    /// reaches it without a retry. Covers every condition that helper ranks: the two
    /// firmware status texts, which are not SCPI-shaped and so have to be matched by substring
    /// (<see cref="DirectoryOpenFailureMarker"/> is prefixed <c>[Error:n]</c> and matches no
    /// classifier at all), and any SCPI error or other <c>Error !!</c> status line.
    /// </summary>
    private static bool ExplainsItsOwnMissingEndMarker(IReadOnlyList<string> lines)
    {
        return lines.Any(line =>
            !string.IsNullOrWhiteSpace(line)
            && (ScpiResponseClassifier.IsErrorResponseLine(line)
                || line.IndexOf(NoSdCardMarker, StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(DirectoryOpenFailureMarker, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    /// <summary>
    /// What a reply to <c>SYSTem:STORage:SD:LISt?</c> turned out to be. One value per way
    /// a listing can be judged, in the order <see cref="ClassifySdListReply"/> tests them.
    /// </summary>
    private enum SdListVerdictKind
    {
        /// <summary>The <c>SYSTem:ERRor?</c> terminator never arrived: the reply is missing or cut short.</summary>
        NoTerminator = 0,

        /// <summary>The device said there is no SD card.</summary>
        NoCard,

        /// <summary>The device said the directory is not on the card.</summary>
        DirectoryNotFound,

        /// <summary>The device could not open the directory for another reason.</summary>
        FilesystemError,

        /// <summary>The reply holds a device error and no listing entries.</summary>
        DeviceError,

        /// <summary>The device's end-of-listing marker says the walk failed.</summary>
        DeviceFailed,

        /// <summary>The device's end-of-listing marker says the walk stopped early.</summary>
        DeviceIncomplete,

        /// <summary>No end-of-listing marker, on firmware that sends one, and nothing in the reply explains why.</summary>
        MarkerMissing,

        /// <summary>The listing is whole.</summary>
        Complete,
    }

    /// <summary>
    /// The verdict on one SD listing reply, as given by <see cref="ClassifySdListReply"/>.
    /// </summary>
    private readonly struct SdListVerdict
    {
        internal SdListVerdict(
            SdListVerdictKind kind,
            IReadOnlyList<string> listing,
            bool hasScpiError,
            SdCardOperationException? error)
        {
            Kind = kind;
            Listing = listing;
            HasScpiError = hasScpiError;
            Error = error;
        }

        internal SdListVerdictKind Kind { get; }

        /// <summary>The reply's lines ahead of the transport terminator.</summary>
        internal IReadOnlyList<string> Listing { get; }

        /// <summary>Whether the listing holds a SCPI error line, with or without entries.</summary>
        internal bool HasScpiError { get; }

        /// <summary>
        /// The exception that reports this verdict; null only when <see cref="IsUsable"/>.
        /// </summary>
        internal SdCardOperationException? Error { get; }

        /// <summary>The listing is the card's contents and may be parsed and cached.</summary>
        internal bool IsUsable => Kind == SdListVerdictKind.Complete;

        /// <summary>
        /// The reply is of a kind that is often transient, so it is worth one more read before
        /// it is acted on: a reply that did not finish, a walk that did not reach its end, or a
        /// SCPI error (frequently the -200 of a bus that was still settling). A usable listing
        /// that carries a stray SCPI error is read again too, and accepted if the error persists.
        /// </summary>
        internal bool ShouldReadAgain =>
            Kind == SdListVerdictKind.NoTerminator
            || Kind == SdListVerdictKind.MarkerMissing
            || HasScpiError;
    }

    /// <summary>
    /// Examines the reply to an SD listing and gives the one verdict on it. The single place
    /// that decides whether a listing is good, used by <see cref="GetSdCardFilesAsync"/> and by
    /// the relist in <see cref="DeleteSdCardFileAsync"/> so a rule added here applies to both
    /// (#942; #887 had gone into one copy only).
    /// </summary>
    /// <param name="lines">
    /// The raw reply, including the <c>SYSTem:ERRor?</c> terminator the exchange asked for.
    /// </param>
    private SdListVerdict ClassifySdListReply(IReadOnlyList<string> lines)
    {
        // Prove the EXCHANGE finished before judging what it contained. A reply cut short by
        // the completion window loses the device's marker too, so nothing else in it can be
        // trusted (#396).
        if (!TrySplitAtSdListTerminator(lines, out var listing))
        {
            return new SdListVerdict(
                SdListVerdictKind.NoTerminator,
                listing,
                hasScpiError: false,
                new SdCardListIncompleteException(lines));
        }

        var hasScpiError = ScpiResponseClassifier.ContainsScpiError(listing);

        // What the device said in words comes next, because it is the more specific answer:
        // "no card" beats "incomplete".
        var deviceError = GetSdCardListError(listing);
        if (deviceError != null)
        {
            var kind = deviceError switch
            {
                SdCardNotPresentException => SdListVerdictKind.NoCard,
                SdCardDirectoryNotFoundException => SdListVerdictKind.DirectoryNotFound,
                SdCardFilesystemException => SdListVerdictKind.FilesystemError,
                _ => SdListVerdictKind.DeviceError,
            };
            return new SdListVerdict(kind, listing, hasScpiError, deviceError);
        }

        // The device's own end-of-listing marker (firmware #794), when it sends one. FAILED
        // means the walk could not open the directory at all; INCOMPLETE means it stopped
        // early. Either way the caller is about to cache this list and answer "is my file on
        // the card?" from it, and a device-truncated listing is the same class of wrong answer
        // as a transport-truncated one, so it raises the same exception.
        var status = SdCardFileListParser.GetListingStatus(listing);
        if (status == SdCardListingStatus.Failed || status == SdCardListingStatus.Incomplete)
        {
            return new SdListVerdict(
                status == SdCardListingStatus.Failed
                    ? SdListVerdictKind.DeviceFailed
                    : SdListVerdictKind.DeviceIncomplete,
                listing,
                hasScpiError,
                new SdCardListIncompleteException(lines));
        }

        // No marker at all, on firmware that emits one, and nothing in the reply to explain
        // why. The transport terminator proves the device answered, so the marker's absence is
        // not a lost reply -- it is a walk that never reached its own end, and the entries in
        // hand (often none) are not the card's contents. Reporting them as the answer is how a
        // card holding five files was rendered as "SD card OK, 0 files" with nothing raising a
        // problem (closes #887).
        if (IsUnexplainedMissingListingEndMarker(listing, status))
        {
            return new SdListVerdict(
                SdListVerdictKind.MarkerMissing,
                listing,
                hasScpiError,
                new SdCardListIncompleteException(lines));
        }

        return new SdListVerdict(SdListVerdictKind.Complete, listing, hasScpiError, error: null);
    }

    /// <summary>
    /// Retrieves the free and total byte counts of the device's SD card.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation, containing the SD card storage info.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware does not recognize the storage query (SCPI -113
    /// "Undefined header"), typically because it predates <see cref="DaqifiDevice.MinSupportedFirmware"/>;
    /// or, over a WiFi/TCP transport, when the firmware predates SD-over-WiFi support
    /// (<see cref="DeviceFeature.SdFileTransferOverWifi"/>) — the storage query drives the SD
    /// card through the same transport gate as the file operations.
    /// </exception>
    /// <exception cref="SdCardOperationException">Thrown when the device returned a SCPI error or an unparseable response.</exception>
    internal async Task<SdCardStorageInfo> GetSdCardStorageAsync(CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        // The storage-space query drives the SD card through the same transport-aware
        // PrepareSdInterface() as LIST/GET/DELETE, so it carries the identical SD-over-WiFi
        // requirement: over WiFi it needs firmware >= v3.7.0 (#598/#599 SPI arbitration) — else
        // it would access the SD card with the LAN still enabled on firmware that never learned
        // to arbitrate the shared bus. Gate it up front for the same reason as its siblings.
        EnsureSdFileTransferSupportedOnTransport();

        cancellationToken.ThrowIfCancellationRequested();

        var wasStreaming = PauseStreamingForSdOperation();

        try
        {
            // Same prepare/finalize pairing as GetSdCardFilesAsync: the SPI bus switch and its
            // settle wait are the exchange's prepare phase and the LAN restore is its finalize
            // phase, so both halves are held by the one lock acquisition rather than only the
            // switch (#407). The settle wait also moves ahead of the exchange's stale-line
            // boundary instead of blocking a thread inside it.
            IReadOnlyList<string> lines;
            var transientRetries = 0;
            var noCardRechecks = 0;
            TimeSpan? delayBeforeNextAttempt = null;

            while (true)
            {
                if (delayBeforeNextAttempt is { } delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false);
                }

                lines = await _host.ExecuteTextCommandAsync(
                    () => _host.Send(ScpiMessageProducer.GetSdSpace),
                    responseTimeoutMs: 3000,
                    cancellationToken: cancellationToken,
                    prepareAsync: PrepareSdInterfaceAndSettleAsync,
                    finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

                // "No SD Card Detected" is not trusted on first sight. The firmware answers it from
                // its last card-detect poll, which can be seconds stale, so it is asked again on the
                // schedule SD_NO_CARD_MAX_RECHECKS describes. A card that is really absent still
                // reports absent, after the last recheck (#896). This is its own schedule, not the
                // transient one below: 100 ms is too soon to outlast a poll that backs off to 5 s.
                if (ContainsNoSdCardMarker(lines))
                {
                    if (noCardRechecks++ < SD_NO_CARD_MAX_RECHECKS)
                    {
                        delayBeforeNextAttempt = SdNoCardRecheckDelay * noCardRechecks;
                        continue;
                    }

                    break;
                }

                // Any other SCPI error is retried as a transient timing failure.
                if (!ScpiResponseClassifier.ContainsScpiError(lines)
                    || transientRetries++ >= SD_LIST_MAX_RETRIES)
                {
                    break;
                }

                delayBeforeNextAttempt = SdInterfaceSettleDelay;
            }

            if (SdCardSpaceParser.TryParseLines(lines, out var storage))
            {
                return storage;
            }

            // Parser failed — translate the firmware response into a typed exception.
            var lastScpiError = ScpiResponseClassifier.GetLastScpiErrorLine(lines);

            if (ContainsNoSdCardMarker(lines))
            {
                throw new SdCardNotPresentException(lines, lastScpiError);
            }

            // A -113 "Undefined header" reply means the firmware doesn't recognize the storage
            // query at all — typically because it predates the version that introduced it — so
            // it gets the typed feature-gating exception instead of a generic operation error.
            // The device's answer is authoritative here, so this throws on the wire response
            // rather than on Supports(); the seam only supplies the required version and board.
            if (lastScpiError != null
                && ScpiResponseClassifier.TryExtractErrorCode(lastScpiError, out var scpiErrorCode)
                && scpiErrorCode == ScpiErrorCodeUndefinedHeader)
            {
                throw _host.CreateFeatureNotSupportedException(DeviceFeature.SdStorageQuery);
            }

            throw new SdCardOperationException(
                lastScpiError != null
                    ? "The SD card storage query failed: " + lastScpiError
                    : "The SD card storage query returned an unparseable response.",
                lines,
                lastScpiError);
        }
        finally
        {
            RestoreStreamingIfNeeded(wasStreaming);
        }
    }

    private static bool ContainsNoSdCardMarker(IReadOnlyList<string> lines)
    {
        return lines.Any(l => l.IndexOf("No SD Card Detected", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <inheritdoc />
    internal async Task<SdCardSpaceCheckResult> CheckSdCardSpaceAsync(
        SdCardCaptureEstimate? plannedCapture = null,
        long minimumFreeBytes = SdCardSpaceCheck.DefaultMinimumFreeBytes,
        CancellationToken cancellationToken = default)
    {
        // Delegates connection / logging-state validation and the typed SD exceptions
        // (no card, old firmware, unparseable response) to GetSdCardStorageAsync.
        var storage = await GetSdCardStorageAsync(cancellationToken).ConfigureAwait(false);

        var result = SdCardSpaceCheck.Evaluate(storage, plannedCapture, minimumFreeBytes);

        // Advisory only — raise the warning but never block the caller from starting logging.
        if (result.ShouldWarn)
        {
            _host.RaiseLowSdSpaceWarning(new LowSdSpaceWarningEventArgs(result));
        }

        return result;
    }


    /// <inheritdoc />
    internal void SetSdCardMinimumFreeSpace(long bytes)
    {
        // Argument validation precedes the connection (state) check so misuse surfaces the same
        // exception type regardless of connection state (matches SetAnalogOutput / SetDioDirection).
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Minimum free space cannot be negative.");
        }

        _host.EnsureConnected();

        _host.Send(ScpiMessageProducer.SetSdMinFreeSpace(bytes));
    }

    /// <inheritdoc cref="ISdCardOperations.SetSdCardCrcProtectionAsync" />
    /// <remarks>
    /// <para>
    /// No bus switch: unlike the file operations this neither mounts the card through the file
    /// system nor turns SD storage on, so it leaves the bus record (issue #941) as it found it.
    /// The firmware is what decides whether SD storage is off and idle, and says so with
    /// <c>-221</c>. Refusing locally on <see cref="SdBusMode.SdBorrowed"/> would not be safe, for
    /// the reason that record's remarks give: a borrow is not always handed back.
    /// </para>
    /// <para>
    /// The device's answer is read from the reply itself. Both transports write a refusal straight
    /// to the wire as <c>**ERROR: &lt;code&gt;, ...</c> when the command pushes it, so the error
    /// line in this exchange belongs to this command. The <c>SYSTem:ERRor:COUNt?</c> sent after it
    /// is the terminator: its reply proves the device got past the set (which can hold its reply
    /// for two seconds), and, unlike <c>SYSTem:ERRor?</c>, it reads the error queue without taking
    /// anything off it. See <see cref="ClearOwnRefusalAsync"/> for what happens to a refusal.
    /// </para>
    /// <para>
    /// Calls run one at a time (<see cref="_sdProtectGate"/>), set and confirmation together, and
    /// a log start waits for the gate to make its claim. The log check is made again once the
    /// gate is held, because a log can start while this call waits behind another; from then to
    /// the end of the call no log start can claim the bus.
    /// </para>
    /// </remarks>
    internal async Task<SdCardCrcProtectionResult> SetSdCardCrcProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();
        _host.EnsureSupported(DeviceFeature.SdCardCrcProtection);

        // The device takes this only with SD storage off and idle. A log this object is running
        // has SD storage on, and turning it off to make room would end the recording, so refuse
        // without sending anything — the answer every other SD operation gives during a log.
        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        await _sdProtectGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Again, now the gate is held: a log may have started while this call waited behind
            // another, and the defensive stop below would end it. Log starts claim the bus only
            // while holding this gate, so the answer holds until the gate is released.
            if (IsBusHeldByLog())
            {
                throw new SdCardBusyException(Array.Empty<string>());
            }

            // Only a stream this object is running is paused, unlike the file operations' stop.
            // The stop is device-wide: on a connection made with PreserveActiveStream it would
            // end an SD log another connection is recording, which this object cannot see. With
            // that log running the device refuses the change itself (-221) and the log goes on.
            var wasStreaming = _host.IsStreaming && PauseStreamingForSdOperation();

            try
            {
                var lines = await _host.ExecuteTextCommandAsync(
                    () =>
                    {
                        _host.Send(ScpiMessageProducer.SetSdCrcProtection(enabled));
                        _host.Send(ScpiMessageProducer.GetSystemErrorCount);
                    },
                    responseTimeoutMs: SD_PROTECT_RESPONSE_TIMEOUT_MS,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await ClearOwnRefusalAsync(lines, cancellationToken).ConfigureAwait(false);

                var result = ClassifySdCrcProtectionReply(lines);

                return result == SdCardCrcProtectionResult.Applied
                    ? await WaitForCardInModeAsync(enabled, cancellationToken).ConfigureAwait(false)
                    : result;
            }
            finally
            {
                RestoreStreamingIfNeeded(wasStreaming);
            }
        }
        finally
        {
            _sdProtectGate.Release();
        }
    }

    /// <summary>
    /// Waits until a card can be mounted again after the device accepted a change, and checks it
    /// came back in the requested mode, so that <see cref="SdCardCrcProtectionResult.Applied"/>
    /// means a card is ready in that mode and an SD operation started straight after
    /// <see cref="SetSdCardCrcProtectionAsync"/> finds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device answers the set as soon as its driver has dropped the card for
    /// re-initialization, and picks the card up again only at its next detect poll. Until then
    /// the card cannot be mounted, and the device does not wait for it: on the bench (firmware
    /// v3.9.0) an SD log started straight after a change found "No SD card detected", fell back
    /// to streaming over USB, and wrote no file, with nothing reported to Core. Measured
    /// unmountable for 0.5–1.0 s after the set returned.
    /// </para>
    /// <para>
    /// Readiness is <c>SYSTem:STORage:SD:SPACe?</c>, the only positive signal firmware v3.9.0 has:
    /// it checks the card is present and then mounts it, with SD storage left off, so the bus
    /// record (issue #941) is untouched. <c>SYSTem:STORage:SD:INFO?</c> cannot do that, since it
    /// answers from cached state whether or not a card is there. Once a mount has worked, though,
    /// its cache describes the card that just mounted, so it is read then to check the mode: the
    /// device keeps one setting, and anything else that changed it in the meantime (another
    /// connection, a raw command) leaves the card in the other mode.
    /// </para>
    /// <para>
    /// No mount within <see cref="SdProtectCardReadyTimeout"/> makes this
    /// <see cref="SdCardCrcProtectionResult.Pending"/>: the device holds the setting but no card
    /// is ready with it. That covers an empty slot, a card that refuses CRC checking (the
    /// firmware never mounts it while protection is on), and a card it cannot mount at all.
    /// </para>
    /// </remarks>
    /// <exception cref="SdCardOperationException">
    /// The card mounted in the other mode: something other than this call changed the setting.
    /// </exception>
    private async Task<SdCardCrcProtectionResult> WaitForCardInModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        var start = Clock.GetTimestamp();

        while (true)
        {
            var lines = await _host.ExecuteTextCommandAsync(
                () =>
                {
                    _host.Send(ScpiMessageProducer.GetSdSpace);
                    _host.Send(ScpiMessageProducer.GetSystemErrorCount);
                },
                responseTimeoutMs: 3000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (SdCardSpaceParser.TryParseLines(lines, out _))
            {
                var info = await _host.ExecuteTextCommandAsync(
                    () => _host.Send(ScpiMessageProducer.GetSdCardInfo),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (TryReadCardCrcMode(info, out var cardMode))
                {
                    if (cardMode == enabled)
                    {
                        return SdCardCrcProtectionResult.Applied;
                    }

                    throw new SdCardOperationException(
                        $"The SD card came back with CRC protection {(cardMode ? "on" : "off")}, not {(enabled ? "on" : "off")} as requested: something other than this call changed the setting while Core was waiting for the card.",
                        info);
                }

                // An unreadable identity line proves nothing either way; try again in the budget.
            }
            else
            {
                await ClearOwnRefusalAsync(lines, cancellationToken).ConfigureAwait(false);
            }

            // No delay between attempts: each exchange already waits out its own completion
            // window, which paces this at a couple of attempts a second.
            if (Clock.GetElapsedTime(start) >= SdProtectCardReadyTimeout)
            {
                return SdCardCrcProtectionResult.Pending;
            }
        }
    }

    /// <summary>
    /// Takes the refusal an exchange of <see cref="SetSdCardCrcProtectionAsync"/> just caused back
    /// off the device's error queue, but only when nothing else is queued.
    /// </summary>
    /// <remarks>
    /// The queue is first-in first-out, and <c>SYSTem:ERRor?</c> pops the OLDEST entry, so with
    /// anything older queued it would discard that entry and leave this one behind. The exchange's
    /// <c>SYSTem:ERRor:COUNt?</c> terminator says how many entries there are: exactly one, with
    /// this exchange's own <c>**ERROR</c> line in the reply, can only be that refusal. With older
    /// entries everything is left in place, in order, for whoever reads the queue; the connect
    /// handshake drains it.
    /// <para>
    /// The count proves ownership only if nothing else reads or adds to the queue between the
    /// exchange that read it and the pop, which are separate exchanges. <see cref="DaqifiStreamingDevice"/>
    /// runs the whole of <see cref="SetSdCardCrcProtectionAsync"/> under its exclusive operation
    /// lock for that reason, as it does the WiFi link-state read.
    /// </para>
    /// </remarks>
    private async Task ClearOwnRefusalAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        if (ScpiResponseClassifier.GetLastScpiErrorLine(lines) == null
            || !TryReadErrorCount(lines, out var queued)
            || queued != 1)
        {
            return;
        }

        await _host.ExecuteTextCommandAsync(
            () => _host.Send(ScpiMessageProducer.GetSystemError),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the <c>SYSTem:ERRor:COUNt?</c> reply: the last line that is a bare non-negative
    /// integer. Nothing else the exchanges here can produce has that shape (the storage-space
    /// reply has a comma, error lines have text).
    /// </summary>
    private static bool TryReadErrorCount(IReadOnlyList<string> lines, out int count)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(lines[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count))
            {
                return true;
            }
        }

        count = 0;
        return false;
    }

    /// <summary>
    /// Reads the card's CRC mode from a <c>SYSTem:STORage:SD:INFO?</c> reply
    /// (<c>mid,"oid","pnm",major.minor,serial,yyyy-mm,crc,resyncs</c>).
    /// </summary>
    /// <remarks>
    /// Read from the end: the two fields firmware v3.9.0 appended are last, and the quoted name
    /// fields before them are the card's own text, which could hold a comma.
    /// </remarks>
    private static bool TryReadCardCrcMode(IReadOnlyList<string> lines, out bool crcOn)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var fields = lines[i].Trim().Split(',');
            if (fields.Length < 8
                || !ulong.TryParse(fields[^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            switch (fields[^2].Trim())
            {
                case "1":
                    crcOn = true;
                    return true;
                case "0":
                    crcOn = false;
                    return true;
            }
        }

        crcOn = false;
        return false;
    }

    /// <summary>
    /// Turns the reply to <c>SYSTem:STORage:SD:PROTect n</c> + <c>SYSTem:ERRor:COUNt?</c> into the
    /// documented outcome of <see cref="SetSdCardCrcProtectionAsync"/>.
    /// </summary>
    /// <remarks>
    /// The codes are the ones <c>SCPI_StorageSDProtectSet</c> pushes at firmware v3.9.0:
    /// <c>-221</c> when SD storage is on or busy (the value is not stored), and <c>-200</c> when
    /// the value is stored but the driver has not applied it within the firmware's two-second
    /// wait, because the SD task is not running. <c>-113</c> is firmware that does not know the
    /// command at all, which the version gate should already have kept from being sent.
    /// </remarks>
    private SdCardCrcProtectionResult ClassifySdCrcProtectionReply(IReadOnlyList<string> lines)
    {
        var refusal = ScpiResponseClassifier.GetLastScpiErrorLine(lines);

        if (refusal == null)
        {
            if (TryReadErrorCount(lines, out _))
            {
                return SdCardCrcProtectionResult.Applied;
            }

            throw new SdCardOperationException(
                "The device did not answer the SD CRC protection command, so whether the setting changed is unknown.",
                lines);
        }

        if (ScpiResponseClassifier.TryExtractErrorCode(refusal, out var code))
        {
            switch (code)
            {
                case ScpiErrorCodeExecutionError:
                    return SdCardCrcProtectionResult.Pending;
                case ScpiErrorCodeSettingsConflict:
                    throw new SdCardBusyException(lines, refusal);
                case ScpiErrorCodeUndefinedHeader:
                    throw _host.CreateFeatureNotSupportedException(DeviceFeature.SdCardCrcProtection);
            }
        }

        throw new SdCardOperationException(
            "The device refused the SD CRC protection command: " + refusal,
            lines,
            refusal);
    }

    /// <inheritdoc cref="ISdCardOperations.GetSdCardCrcProtectionAsync" />
    /// <remarks>
    /// A plain query: no bus switch and no stream pause, so it is safe during an SD log, where a
    /// pause would end the recording.
    /// </remarks>
    internal async Task<bool> GetSdCardCrcProtectionAsync(CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();
        _host.EnsureSupported(DeviceFeature.SdCardCrcProtection);

        cancellationToken.ThrowIfCancellationRequested();

        var lines = await _host.ExecuteTextCommandAsync(
            () => _host.Send(ScpiMessageProducer.GetSdCrcProtection),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // The last readable answer, the line closest to the query.
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            switch (lines[i].Trim())
            {
                case "1":
                    return true;
                case "0":
                    return false;
            }
        }

        var lastScpiError = ScpiResponseClassifier.GetLastScpiErrorLine(lines);

        if (lastScpiError != null
            && ScpiResponseClassifier.TryExtractErrorCode(lastScpiError, out var code)
            && code == ScpiErrorCodeUndefinedHeader)
        {
            throw _host.CreateFeatureNotSupportedException(DeviceFeature.SdCardCrcProtection);
        }

        throw new SdCardOperationException(
            lastScpiError != null
                ? "The SD CRC protection query failed: " + lastScpiError
                : "The SD CRC protection query returned no readable answer.",
            lines,
            lastScpiError);
    }

    /// <summary>
    /// Starts logging data to the SD card. Compatibility overload preserving the original
    /// <see cref="Task"/> return; use <see cref="StartSdCardLoggingSessionAsync"/> to also learn
    /// the effective on-card file name.
    /// </summary>
    /// <param name="fileName">The log file name, or null/empty to auto-generate a timestamped name.</param>
    /// <param name="channelMask">Optional decimal channel bitmask; null/empty uses the current config.</param>
    /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    internal Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
        => StartSdCardLoggingSessionAsync(fileName, channelMask, format, cancellationToken);

    /// <summary>
    /// Starts logging data to the SD card and returns the effective session details.
    /// </summary>
    /// <param name="fileName">
    /// The name of the log file. If null or empty, a timestamped name is generated automatically
    /// using the pattern "log_YYYYMMDD_HHMMSS" with an extension matching <paramref name="format"/>
    /// (.bin for Protobuf, .json for JSON, .csv for CSV).
    /// </param>
    /// <param name="channelMask">
    /// Optional decimal bitmask string to enable specific ADC channels (e.g. "3" enables channels 0 and 1).
    /// The firmware parses this as a decimal integer where each bit enables a channel.
    /// If null or empty, the current device channel configuration is used.
    /// </param>
    /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// A task that resolves to an <see cref="SdCardLoggingSession"/> carrying the effective on-card
    /// file name (supplied or auto-generated) and the logging format.
    /// </returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when an SD log is already starting, recording or stopping.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    internal async Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        if (!_host.IsUsbConnection)
        {
            throw new InvalidOperationException(
                "SD card logging requires a USB/serial connection. Starting a logging session " +
                "disables the LAN interface to give the SD card the shared SPI bus, which over " +
                "a network connection would drop the very link the command arrived on. " +
                "Listing, downloading and deleting SD files do work over WiFi on firmware " +
                "v3.7.0 and later.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var extension = format switch
        {
            SdCardLogFormat.Json => ".json",
            SdCardLogFormat.Csv => ".csv",
            _ => ".bin",
        };

        var logFileName = !string.IsNullOrWhiteSpace(fileName)
            ? fileName!
            // Local wall clock, read through the device's clock (issue #637): the name a
            // user sees on the card should match the clock on the wall where the device is,
            // and reading it through the provider is what makes the generated name
            // deterministic in a test instead of whatever second the run landed on.
            : $"log_{Clock.GetLocalNow().DateTime:yyyyMMdd_HHmmss}{extension}";

        // Before the first send: a bad name must not leave LAN switched off behind it.
        ScpiMessageProducer.ValidateSdFileName(logFileName);

        // SdCardLogFormat integer values map 1:1 to the SYSTem:STReam:FORmat SCPI argument
        var formatCommand = ScpiMessageProducer.SetStreamFormat((int)format);

        // Under the CRC protection gate: a change that is running has already checked for a log
        // and is about to send, or has sent, its defensive stream stop, which would end this one.
        // Waiting here lets it finish and confirm the card first.
        await _sdProtectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool wasLiveStreaming;
        try
        {
            // Read before the claim: a start that is cancelled or fails runs the stop sequence,
            // which stops a live stream that was running before it and has nothing to do with it.
            wasLiveStreaming = _host.IsStreaming && !_host.IsLoggingToSdCard;

            // Recorded before the first command, so the other SD operations — and a second start —
            // refuse from here on rather than only once the last command has gone (issue #941).
            ClaimBusForLogStart();
        }
        finally
        {
            _sdProtectGate.Release();
        }

        try
        {
            // SD card and LAN share the SPI bus on the hardware, so LAN must be
            // disabled before the SD card can be used.
            _host.Send(ScpiMessageProducer.DisableNetworkLan);
            await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);

            _host.Send(ScpiMessageProducer.EnableStorageSd);
            await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);

            // Route the data stream to the SD card interface.
            _host.Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.SdCard));
            await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);

            _host.Send(ScpiMessageProducer.SetSdLoggingFileName(logFileName));
            await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);

            _host.Send(formatCommand);
            await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(channelMask))
            {
                _host.Send(ScpiMessageProducer.EnableAdcChannels(channelMask));
                await Task.Delay(SdLogStartCommandSpacing, Clock, cancellationToken).ConfigureAwait(false);
            }

            // Checked here, once the stream is routed to the card in its format and channel set:
            // that is the configuration the device checks the rate against. A rate inherited
            // from a faster USB stream would otherwise be refused, and nothing recorded.
            await _host.LowerInheritedStreamingFrequencyAsync(cancellationToken).ConfigureAwait(false);

            _host.Send(ScpiMessageProducer.StartStreaming(_host.StreamingFrequency));

            _host.BeginSdCardLogSession();

            EnterBusMode(SdBusMode.Logging);
        }
        catch
        {
            // A cancellation or a failed send part-way through leaves the device wherever the
            // commands already sent put it: LAN off, SD storage on, and possibly the stream
            // routed to the card in a text format — with nothing recording, and nothing in
            // Core saying so. The stop sequence sets each of those back to its default, so it
            // is the right cleanup after any step (issue #941). Best-effort: if the link is
            // what failed, the stop cannot go out either, and the exception that reaches the
            // caller must be the start's own.
            try
            {
                EndLoggingSession();
            }
            catch
            {
                // The start's exception is the one to report.
            }

            // The cleanup's StopStreamData also silenced a live stream that was running before
            // this start. Its consumer would otherwise go quiet with no error, the failure #533
            // fixed for the other SD operations, so it is resumed the same way.
            RestoreStreamingIfNeeded(wasLiveStreaming);

            throw;
        }

        return new SdCardLoggingSession(logFileName, format);
    }

    /// <summary>
    /// Stops logging data to the SD card.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    internal Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected(cancellationToken);

        EndLoggingSession();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends everything that ends a logging session and puts the device back as live streaming
    /// and the other SD operations expect it. The one path for
    /// <see cref="StopSdCardLoggingAsync"/> and for
    /// <see cref="DaqifiStreamingDevice.StopStreaming"/> during a log, so the two cannot drift
    /// (issue #932).
    /// </summary>
    /// <remarks>
    /// The session ends as soon as the stop has gone out, not after the last command: the firmware
    /// closes the log on the stop itself, so from there the card is free. If a later command
    /// fails, the connection has failed with it. <see cref="StopSdCardLoggingAsync"/> sends the
    /// whole sequence again however often it is called, and the next initialization repairs the
    /// format and the routing. A stop that fails leaves the session as it was.
    /// </remarks>
    internal void EndLoggingSession()
    {
        EnterBusMode(SdBusMode.LogEnding);

        try
        {
            SendLogStopSequence();
        }
        finally
        {
            // A stop that could not be sent leaves the session, and so the bus, with the log.
            EnterBusMode(_host.IsLoggingToSdCard ? SdBusMode.Logging : SdBusMode.LanActive);
        }
    }

    private void SendLogStopSequence()
    {
        // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
        _host.Send(ScpiMessageProducer.StopStreaming);
        _host.EndStreamingSession();

        // Put the stream encoding back to protobuf, the format the library runs live streaming in
        // (DaqifiDevice.InitializeAsync sets it at connect time, and the frame decoder reads
        // nothing else). StartSdCardLoggingSessionAsync sets SYSTem:STReam:FORmat to the session's
        // log format, and the firmware holds ONE encoding for every interface
        // (SCPIInterface.c, streaming.c), so a JSON or CSV session left live streaming emitting
        // text that the protobuf framing silently dropped until the next reconnect (closes #851).
        //
        // Protobuf rather than "whatever the format was before the session": the library keeps no
        // record of the device's previous encoding, and it cannot read one back here — the query
        // needs a text exchange, which the firmware refuses while the SD manager is still busy.
        // Protobuf is not a guess, though: it is the format the library itself establishes on
        // every connection, so for any session this library started it IS the format the session
        // started from. A caller that chose a different live format by hand must set it again.
        //
        // Sent right after the stop and ahead of the bus teardown: it mirrors the position the
        // start sends it from, and it is the one command here that applies on every transport,
        // so it stays out of the USB-only block below.
        _host.Send(ScpiMessageProducer.SetProtobufStreamFormat);

        _host.Send(ScpiMessageProducer.DisableStorageSd);

        // Restore stream interface to USB so subsequent non-SD operations work.
        if (_host.IsUsbConnection)
        {
            _host.Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb));

            // Re-enable LAN interface. StartSdCardLoggingAsync disables LAN because
            // the SD card and WiFi/LAN share the SPI bus on the hardware.
            //
            // Only over USB, and for the same reason PrepareLanInterface() is transport-aware:
            // over WiFi/TCP the LAN was never disabled (nothing can disable it from the other
            // end of the connection it carries), and LAN:ENAbled 1 re-initializes the WiFi
            // module — which would drop the very link this command arrived on. A session that
            // logged over USB, disconnected, and came back over WiFi could otherwise call this
            // and cut itself off (#327).
            _host.Send(ScpiMessageProducer.EnableNetworkLan);
        }
    }

    /// <summary>
    /// Deletes a file from the SD card.
    /// </summary>
    /// <param name="fileName">The name of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    /// <exception cref="SdCardListIncompleteException">
    /// Thrown when the listing that follows the delete cannot be trusted as complete, by the
    /// same rules <see cref="GetSdCardFilesAsync"/> applies. <see cref="SdCardFiles"/> keeps the
    /// value it had before the call.
    /// </exception>
    internal async Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        EnsureSdFileTransferSupportedOnTransport();

        cancellationToken.ThrowIfCancellationRequested();

        ScpiMessageProducer.ValidateSdFileName(fileName);

        var wasStreaming = PauseStreamingForSdOperation();

        try
        {
            // Same prepare/finalize treatment as GetSdCardFilesAsync, for the same reasons — the
            // SPI switch stays serialized against competing text exchanges, its settle wait stays
            // outside the stale-line boundary, and the restore stays under the same lock as the
            // switch instead of running from a finally after the lock has been dropped. The
            // consequence of a stale line is milder here (delete keys off ContainsScpiError, so it
            // would mean a pointless delete-and-relist retry rather than a bad listing) but it is
            // the same defect.
            var lines = await _host.ExecuteTextCommandAsync(
                () =>
                {
                    _host.Send(ScpiMessageProducer.DeleteSdFile(fileName));
                    _host.Send(ScpiMessageProducer.GetSdFileList);

                    // Transport terminator, exactly as the read path sends it.
                    // Without it a reply cut short by the completion window is
                    // indistinguishable from a complete one that carried no
                    // device marker -- and the second of those is legitimate
                    // (pre-#796 firmware), so the status alone cannot tell them
                    // apart. This is what makes "the exchange finished" a fact
                    // rather than an assumption.
                    _host.Send(ScpiMessageProducer.GetSystemError);
                },
                responseTimeoutMs: 3000,
                // Same completion window the read path uses for the same
                // command. Without it this exchange takes the 250 ms default,
                // and the firmware walks the directory tree between chunks --
                // so a merely-slow listing is cut off BEFORE its terminator
                // arrives. That reply is refused (NoTerminator), never cached,
                // so too short a window would make a delete report an
                // incomplete listing for a card that answered correctly.
                completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                cancellationToken: cancellationToken,
                prepareAsync: PrepareSdInterfaceAndSettleAsync,
                finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

            if (ScpiResponseClassifier.ContainsScpiError(lines))
            {
                for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SdInterfaceSettleDelay, Clock, cancellationToken).ConfigureAwait(false);

                    lines = await _host.ExecuteTextCommandAsync(
                        () =>
                        {
                            _host.Send(ScpiMessageProducer.DeleteSdFile(fileName));
                            _host.Send(ScpiMessageProducer.GetSdFileList);
                            _host.Send(ScpiMessageProducer.GetSystemError);
                        },
                        responseTimeoutMs: 3000,
                        // The retry needs the window as much as the first
                        // attempt: it is the same listing, and a retry that
                        // truncates hands the same partial list to the same
                        // cache.
                        completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                        cancellationToken: cancellationToken,
                        prepareAsync: PrepareSdInterfaceAndSettleAsync,
                        finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

                    if (!ScpiResponseClassifier.ContainsScpiError(lines))
                    {
                        break;
                    }
                }
            }

            // The error from an exchange that actually SENT the delete, taken
            // before the relist retry below can reassign `lines`. Without this
            // capture the check further down reads whichever reply came last,
            // and a transient error from a LIST-only retry -- a bus still
            // settling, the -200 this file documents elsewhere -- was reported
            // as "the delete operation failed" for a delete that had already
            // succeeded and was never re-sent.
            var deleteExchangeError = ScpiResponseClassifier.ContainsScpiError(lines)
                ? ScpiResponseClassifier.GetLastScpiErrorLine(lines)
                : null;

            // The verdict on the listing that came back with the delete, from the
            // same method the read path uses (#942).
            var verdict = ClassifySdListReply(lines);

            if (deleteExchangeError == null)
            {
                // A reply that did not finish, or a walk that did not reach its
                // end, with no error to explain it, is a one-off stall, and the
                // read path gives that a second attempt for the same reason.
                // Re-LIST only: the DELETE was accepted -- nothing reported
                // otherwise -- so re-sending it would ask the device to remove a
                // file that is already gone and turn a transient stall into a
                // hard error. That is also why this is a separate loop from the
                // error retry above, which re-sends both deliberately because
                // there the delete itself may not have landed.
                for (var retry = 0; retry < SD_LIST_MAX_RETRIES && verdict.ShouldReadAgain; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SdInterfaceSettleDelay, Clock, cancellationToken).ConfigureAwait(false);

                    lines = await _host.ExecuteTextCommandAsync(
                        () =>
                        {
                            _host.Send(ScpiMessageProducer.GetSdFileList);
                            _host.Send(ScpiMessageProducer.GetSystemError);
                        },
                        responseTimeoutMs: 3000,
                        completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                        cancellationToken: cancellationToken,
                        prepareAsync: PrepareSdInterfaceAndSettleAsync,
                        finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

                    verdict = ClassifySdListReply(lines);
                }
            }

            // The DELETE's own outcome, judged before anything about the
            // listing. The retry loop above exits either way, so a delete the
            // device refused on BOTH attempts arrives here with its error line
            // intact -- and the listing's verdict alone will not catch it: a
            // SCPI error is not held against a reply that also carries
            // real entries, which is the normal case (you deleted one file, the
            // others are still there). That escape is right for the read path,
            // where a stray error has no bearing on the listing; it is wrong
            // here, where the error line IS the answer to "did the delete
            // happen?". Reproduced: a card holding fileA and fileB, a DELETE
            // refused twice, returned success with fileA still in the cache.
            if (deleteExchangeError != null)
            {
                // ...unless the card itself says the file is gone. An error
                // line carries no origin: the exchange sends DELETE, LIST and
                // SYSTem:ERRor? together, and the LIST has failure modes of
                // its own (busy, the 10 s large-directory timeout) that have
                // nothing to do with the delete. The retry then re-sends the
                // DELETE for a file the first attempt already removed, the
                // device answers "delete operation failed" because it is not
                // there, and that second, genuine error would be reported as
                // the delete failing.
                //
                // The listing in the same exchange settles it. If it rendered
                // completely and the file is absent, the delete happened --
                // whatever the error line was about. Verifying the outcome
                // beats attributing the error, which the wire format does not
                // let us do.
                // Absence only proves anything if the listing is WHOLE. A
                // terminated reply says the exchange finished, not that the
                // device's walk reached the end of the tree, and a listing
                // can be missing the target because it stopped early --
                // reading that as "deleted" is the same wrong answer pointed
                // the other way. Only the device's own Complete marker rules
                // it out, so against firmware that sends no marker the error
                // stands, exactly as it did before this check existed.
                //
                // Asked of the terminator and the marker, not of the verdict:
                // the last file on the card leaves a listing that is nothing
                // but the error line and "__END_OF_LIST__ OK", which the
                // verdict calls a DeviceError. The walk still finished and
                // the file is still absent, so the delete is not to blame --
                // the listing's own error is what is reported, below.
                var target = System.IO.Path.GetFileName(fileName);
                var goneFromCard = verdict.Kind != SdListVerdictKind.NoTerminator
                    && SdCardFileListParser.GetListingStatus(verdict.Listing)
                        == SdCardListingStatus.Complete
                    && !SdCardFileListParser.ParseFileList(verdict.Listing).Any(
                        f => string.Equals(f.FileName, target,
                                           StringComparison.OrdinalIgnoreCase));
                if (!goneFromCard)
                {
                    throw new SdCardOperationException(
                        "The SD card delete operation failed: " + deleteExchangeError,
                        lines,
                        deleteExchangeError);
                }
            }

            // Same rule as the read path, with one intended difference: a
            // directory that is not on the card is an empty list there and an
            // exception here, because it leaves "is the file I named gone?"
            // unanswered. Anything short of a whole listing must not become the
            // cached answer to "what is on the card?". This refresh follows a
            // DELETE, so the cache it overwrites is exactly what a caller
            // consults next to decide whether the file is gone -- and a partial
            // list makes every absence look confirmed. The cache is left
            // untouched rather than replaced with a list we know is short.
            if (!verdict.IsUsable)
            {
                throw verdict.Error!;
            }

            // One rule this path keeps tighter than the read path. On firmware
            // that sends the end marker, a listing with entries, a SCPI error
            // line and no marker is usable to the verdict: the error line
            // counts as the explanation for the missing marker (#887, read
            // path). Here the same reply is a walk that may have stopped
            // early, about to replace the cache a caller reads next to learn
            // what the delete left behind, so it is refused and the cache is
            // kept.
            if (verdict.HasScpiError
                && SdCardFileListParser.GetListingStatus(verdict.Listing)
                    == SdCardListingStatus.Unterminated
                && _host.Supports(DeviceFeature.SdListingEndMarker))
            {
                throw new SdCardListIncompleteException(lines);
            }

            _sdCardFiles = SdCardFileListParser.ParseFileList(verdict.Listing);
        }
        finally
        {
            RestoreStreamingIfNeeded(wasStreaming);
        }
    }

    /// <summary>
    /// Formats the entire SD card, erasing all data.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    internal Task FormatSdCardAsync(CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        cancellationToken.ThrowIfCancellationRequested();

        var wasStreaming = PauseStreamingForSdOperation();

        try
        {
            _host.Send(ScpiMessageProducer.EnableStorageSd);

            // SD storage is on from here and nothing below switches it off, so say so. Not a
            // record anything refuses on; the next list, delete or log stop puts it back.
            MoveBusMode(SdBusMode.LanActive, SdBusMode.SdBorrowed);

            _host.Send(ScpiMessageProducer.FormatSdCard);

            // Nothing is sent after the format, and SD storage is left ON (issue #941, still
            // open for format). The firmware arms the format and returns at once, and it
            // answers SD:ENAble 0 with -200 for as long as the format runs, so a restore sent
            // here is refused every time: the card stays on anyway and each format leaves an
            // entry in the SCPI error queue. Handing the bus back needs a wait on
            // SYSTem:STORage:SD:FORmat? first, which would make this call block for the whole
            // format; that is left to a follow-up.
        }
        finally
        {
            // In a finally, not only on the path where both sends succeed: a stream that was
            // silenced by the stop above must come back even if EnableStorageSd or FormatSdCard
            // itself throws (e.g. the device dropped mid-command), the same as every other SD
            // operation in this file now guarantees.
            RestoreStreamingIfNeeded(wasStreaming);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Downloads a file from the device's SD card, over USB or over WiFi/TCP.
    /// </summary>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="destinationStream">The stream to write file contents to.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata about the downloaded file.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">Thrown over a WiFi/TCP transport when the firmware predates SD-over-WiFi file transfer.</exception>
    /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="SdCardEmptyTransferException">
    /// Thrown when the device serves a marker-only (0-byte) transfer across all retry attempts
    /// for a file the last <see cref="GetSdCardFilesAsync"/> listing reported as non-empty (or
    /// whose listed size is unknown), indicating its SD subsystem is not ready. A file the
    /// listing reports as 0 bytes downloads successfully as a legitimate empty file.
    /// On firmware v3.9.0+ the device's error queue is read first, and a reason found there
    /// raises <see cref="SdCardFileNotFoundException"/> or <see cref="SdCardFilesystemException"/>
    /// instead; the same read applies to a listed 0-byte file.
    /// </exception>
    /// <exception cref="SdCardFileNotFoundException">
    /// Thrown on firmware v3.9.0+ when the device could not open the file because the name does
    /// not lead to one: it is not on the card (<c>-256</c>) or the device rejected the name
    /// (<c>-257</c>). On older firmware the same request raises
    /// <see cref="SdCardEmptyTransferException"/>, because the device reports nothing more.
    /// </exception>
    /// <exception cref="SdCardFilesystemException">
    /// Thrown on firmware v3.9.0+ when the device could not open the file because its storage is
    /// unusable (<c>-250</c>): no card mounted, access denied, or out of file handles.
    /// </exception>
    /// <exception cref="SdCardTransferErrorException">
    /// Thrown when the device ends the transfer with the transfer-error marker it gained in
    /// firmware v3.7.3 — it hit a read error part-way through the file. What was written to
    /// <paramref name="destinationStream"/> is genuine but partial file content, and unlike the
    /// stall it replaces, it names the device rather than the transport as the cause.
    /// </exception>
    /// <exception cref="SdCardTruncatedTransferException">
    /// Thrown when the transfer ends at the end-of-file marker with fewer bytes than the last
    /// <see cref="GetSdCardFilesAsync"/> listing reported for the file — the device served a
    /// short reply, such as a SCPI error line, in place of the file. Anything already written
    /// to <paramref name="destinationStream"/> is not the file and must be discarded.
    /// </exception>
    /// <exception cref="SdCardTransferStalledException">
    /// Thrown when the transfer stops making progress before the end-of-file marker arrives:
    /// the transport returned an empty read, closed, or — the only signal a socket gives —
    /// went quiet for longer than <see cref="DaqifiStreamingDevice.SdCardTransferIdleTimeout"/>.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the download does not finish within <see cref="DaqifiStreamingDevice.SdCardDownloadTimeout"/>.
    /// The deadline is enforced by this method itself, so it still applies when the transfer
    /// is parked in a call that cannot observe a cancellation token (#399).
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>
    /// The transfer holds the device's operation lock for its whole duration (#493), so while it
    /// runs a text query from another thread waits and a <see cref="DaqifiDevice.Send{T}"/> from
    /// another thread is deferred and replayed afterwards. That is what keeps a status poll from
    /// putting a second reader on the stream, and a stray command's reply out of the file's
    /// bytes. The cost is that an unrelated query on another thread can now wait for the whole
    /// download rather than corrupting it; pass it a cancellation token if it cannot.
    /// <para>
    /// On a timeout — or a cancellation the parked transfer cannot itself observe — the
    /// in-flight transfer is <b>abandoned</b> rather than awaited: it may be blocked in native
    /// serial I/O that no token can interrupt, and waiting for it is the hang this method
    /// exists to bound. The abandoned transfer's token is cancelled first, so it unwinds at
    /// its next token check — but that check is only reached once whatever it is blocked in
    /// returns, which may be never. Two consequences for callers: it can still write to
    /// <paramref name="destinationStream"/> after this method has thrown, so the stream must
    /// not be reused for anything else; and the device is left mid-<c>SD:GET</c> with its
    /// reader stopped, so reconnecting (or power-cycling, if its SD subsystem is
    /// genuinely wedged) is the reliable way to resume normal operation.
    /// </para>
    /// <para>
    /// An abandoned transfer also still holds the operation lock. Its token is cancelled on the
    /// way out, so the usual case — a read that returns late — unwinds at its next token check
    /// and releases it. A read that never returns keeps the lock, and keeps the transport stream
    /// with it: a text query blocked on that lock is blocked on a stream nothing could safely
    /// have used anyway, and the reconnect this case already calls for is what clears it.
    /// </para>
    /// <para>
    /// The LAN interface is deliberately <b>not</b> restored in that case: the abandoned
    /// transfer still owns the transport, and putting the restore commands onto a link it is
    /// still reading would only add traffic to a device that has already stopped answering. The
    /// reconnect the caller needs anyway re-establishes the interface. On every other outcome —
    /// success, a stall, a cancellation the transfer did observe — the restore runs as before.
    /// </para>
    /// <para>
    /// Until an abandoned transfer unwinds it still owns the transport, so a further download
    /// on the same device fails fast with <see cref="InvalidOperationException"/> rather than
    /// putting a second reader on the same stream. A caller looping over many files against a
    /// wedged card therefore gets one timeout and then immediate, cheap failures — not a
    /// growing pile of blocked threads.
    /// </para>
    /// </remarks>
    internal async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
        string fileName,
        Stream destinationStream,
        IProgress<SdCardTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _host.EnsureConnected();

        // Over WiFi/TCP this requires firmware >= v3.7.0 (#598/#599); over USB it is always
        // available on SD-capable firmware. Older firmware over WiFi gets a typed
        // FeatureNotSupportedException instead of the old blanket USB-only rejection (ADR 0001).
        EnsureSdFileTransferSupportedOnTransport();

        ScpiMessageProducer.ValidateSdFileName(fileName);
        ArgumentNullException.ThrowIfNull(destinationStream);

        cancellationToken.ThrowIfCancellationRequested();

        if (IsBusHeldByLog())
        {
            throw new SdCardBusyException(Array.Empty<string>());
        }

        var wasStreaming = PauseStreamingForSdOperation();

        // The download's own elapsed clock (issue #637): it feeds the receiver's remaining
        // budget below and the duration reported on the result, and both are counted in the
        // seconds-to-minutes range SdCardDownloadTimeout allows.
        var clock = Clock;
        var startedAt = clock.GetTimestamp();
        long fileSize = 0;
        var budget = _host.SdCardDownloadTimeout;

        // Set when the transfer was given up on and left running (#399/#401). Read only by the
        // restore below, on this same async flow.
        var workerAbandoned = false;

        // From firmware v3.9.0 an empty reply is not the whole answer: a file the device could
        // not open also ends with a bare EOF marker, and the device queues the reason on this
        // transport (#959). Below it the queue never says why, so it is not read.
        var readsOpenFailure = _host.Supports(DeviceFeature.SdGetOpenFailureError);

        try
        {
            await RunWithHardDeadlineAsync(
                token => readsOpenFailure
                    ? _host.RunExclusiveAsync(DownloadReadingOpenFailureAsync, token)
                    : ReceiveSdFileAsync(attempt: 0, token),
                budget,
                fileName,
                cancellationToken,
                onWorkerAbandoned: () => workerAbandoned = true).ConfigureAwait(false);

            // The v3.9.0+ download. All of it runs in one exclusive block, so no other caller's
            // command lands between the GET and the reads of the error queue around it: a query
            // could take the GET's error off the queue, and a failing command could add its own.
            async Task DownloadReadingOpenFailureAsync(CancellationToken token)
            {
                // Empty the queue before the GET, so what it holds afterwards came from this
                // download and not from an earlier command (a stale -256 would turn a legitimately
                // empty file into "not found"). Read with SYSTem:ERRor? rather than cleared with
                // *CLS, which would also reset the status and event registers. Only a read that
                // reached "No error" is a boundary: one that stopped early may have left an older
                // entry behind, and then nothing read after the GET is blamed on it.
                var boundaryConfirmed = await ReadErrorQueueToEmptyAsync(token).ConfigureAwait(false);

                try
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await ReceiveSdFileAsync(attempt, token).ConfigureAwait(false);
                            break;
                        }
                        // The same one retry as below v3.9.0 (#264), but in a capture of its own,
                        // because the queue can only be read between captures. The read gives the
                        // retry its own clean queue: the first attempt's error is not its answer.
                        catch (SdCardEmptyTransferException) when (attempt < SD_LIST_MAX_RETRIES)
                        {
                            boundaryConfirmed = await ReadErrorQueueToEmptyAsync(token).ConfigureAwait(false);
                        }
                    }
                }
                // Read the device's reason for an empty reply before blaming the SD subsystem.
                catch (SdCardEmptyTransferException)
                {
                    await ThrowIfSdGetOpenFailedAsync(fileName, boundaryConfirmed, token).ConfigureAwait(false);
                    throw;
                }

                // A file the listing reported as 0 bytes downloads as a legitimate empty file
                // without throwing, but by now it may have been deleted, and that answer is the
                // same empty reply. Read the queue here too: it tells the two apart, and it is the
                // only read that takes the device's queued error off the queue, where it would
                // otherwise be left for the next exchange to trip over (#898).
                if (fileSize == 0)
                {
                    await ThrowIfSdGetOpenFailedAsync(fileName, boundaryConfirmed, token).ConfigureAwait(false);
                }
            }

            async Task ReceiveSdFileAsync(int attempt, CancellationToken token)
            {
                await _host.ExecuteRawCaptureAsync(async (stream, ct) =>
                {
                    // Prepare SD card interface. Transport-aware: over USB it hands the shared
                    // SPI bus to the card, over WiFi/TCP it leaves the LAN up because the reply
                    // comes back over that very connection (#598/#599). A v3.9.0+ retry finds
                    // the bus already prepared by the first attempt.
                    if (attempt == 0)
                    {
                        PrepareSdInterface();
                    }

                    // Let the interface switch settle before the card is asked for anything —
                    // the same wait the LIST/DELETE/space exchanges take for the same reason.
                    await Task.Delay(SdInterfaceSettleDelay, Clock, ct).ConfigureAwait(false);

                    // Send the SCPI command to request the file
                    _host.Send(ScpiMessageProducer.GetSdFile(fileName));

                    // Receive the file data. A marker-only (0-byte) transfer for a file the
                    // listing reports as non-empty means the device's SD subsystem wasn't ready
                    // when it opened the file - the same kind of transient condition
                    // GetSdCardFilesAsync's LIST retry already absorbs - so retry the GET a
                    // bounded number of times before giving up (see #264). Passing the listed
                    // size keeps that retry off a genuinely 0-byte file, which is a legitimate
                    // empty download rather than a wedged subsystem (#398 gap 2).
                    // The receiver is told what its transport's silence means. Over USB serial a
                    // zero-length read is the per-read ReadTimeout firing on a device that is
                    // merely quiet; over TCP it is the peer's FIN and nothing else, and the
                    // socket keeps reporting itself readable either way. Over TCP the socket
                    // also never surfaces silence at all — ReadAsync ignores the receive
                    // timeout — so the inactivity window is the only thing standing between a
                    // device that stopped answering and the full 30-minute budget (#327).
                    var receiver = new SdCardFileReceiver(
                        stream,
                        zeroLengthReadMeansClosed: !_host.IsUsbConnection,
                        idleTimeout: _host.SdCardTransferIdleTimeout);
                    var listedFileSizeBytes = TryGetListedFileSize(fileName);
                    long bytesReceived;
                    var retries = 0;
                    while (true)
                    {
                        try
                        {
                            // Each attempt gets what is left of the overall budget, never a
                            // fresh full one: retries must not be able to push the total past
                            // the deadline the caller was promised.
                            bytesReceived = await receiver.ReceiveAsync(
                                destinationStream,
                                fileName,
                                progress,
                                timeout: RemainingBudget(budget, clock.GetElapsedTime(startedAt)),
                                cancellationToken: ct,
                                listedFileSizeBytes: listedFileSizeBytes).ConfigureAwait(false);
                            break;
                        }
                        // Only the marker-only case is retried. A SHORT transfer
                        // (SdCardTruncatedTransferException, #539) is not, even though it is
                        // the same kind of transient device condition: by then those bytes
                        // have already been written to the caller's destinationStream, which
                        // this method cannot rewind, so a second attempt would append to the
                        // garbage rather than replace it and could land on the listed size by
                        // sheer accumulation. Letting it out is the honest answer — the caller
                        // discards the stream and retries the download. A transfer that ended
                        // on the device's own read-error marker (SdCardTransferErrorException,
                        // #720) is left alone for the same reason. On v3.9.0+ the retry is the
                        // caller's, in a capture of its own (see above).
                        catch (SdCardEmptyTransferException) when (!readsOpenFailure && retries < SD_LIST_MAX_RETRIES)
                        {
                            retries++;
                            await Task.Delay(SdInterfaceSettleDelay, Clock, ct).ConfigureAwait(false);
                            _host.Send(ScpiMessageProducer.GetSdFile(fileName));
                        }
                    }

                    fileSize = bytesReceived;
                }, token).ConfigureAwait(false);
            }
        }
        finally
        {
            // Restore the LAN interface — but NOT when the transfer was abandoned. An abandoned
            // worker is still alive and still owns the transport (that is why the download gate
            // is not released until it finally unwinds), so sending the restore now would put
            // SCPI commands onto a link a transfer is still reading, on top of a device that has
            // already stopped answering. There is nothing to gain: the caller is told to
            // reconnect or power-cycle, and both re-establish the interface anyway (#399/#401).
            if (!workerAbandoned && _host.IsConnected)
            {
                try
                {
                    PrepareLanInterface();
                }
                catch
                {
                    // Best-effort restoration; the device may have disconnected
                }

                // Same "still owns the transport" reasoning as the LAN restore above applies to
                // resuming a live stream (#533): an abandoned worker can still be reading the
                // transport, and StartStreaming's Send would be a second writer on it.
                RestoreStreamingIfNeeded(wasStreaming);
            }
        }

        return new SdCardDownloadResult(fileName, fileSize, clock.GetElapsedTime(startedAt));
    }

    /// <summary>
    /// Looks up the size the most recent directory listing reported for a file. Returns null
    /// ("unknown", which the receiver treats conservatively) when no listing has been fetched,
    /// when the listing did not include this file or a size for it, or when more than one
    /// listed entry shares the name.
    /// </summary>
    private long? TryGetListedFileSize(string fileName)
    {
        // Snapshot the field: GetSdCardFilesAsync replaces the list wholesale, so a
        // concurrent refresh swaps the reference rather than mutating what we enumerate.
        var listedFiles = _sdCardFiles;

        long? matchedSize = null;
        var matched = false;

        foreach (var file in listedFiles)
        {
            // FAT names are case-insensitive. The listing keeps only the leaf name, so the
            // same name can appear twice from different directories; that is ambiguous and
            // an over-confident size here would wave through the very failure (a wedged
            // subsystem serving nothing) the empty-transfer guard exists to catch.
            if (!string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (matched)
            {
                return null;
            }

            matched = true;
            matchedSize = file.SizeInBytes;
        }

        return matchedSize;
    }

    /// <summary>
    /// Reads the device's SCPI error queue after an <c>SD:GET</c> that came back empty, and throws
    /// if the device queued an open failure for it there (firmware v3.9.0+, #959). Returns when
    /// it did not, which leaves the caller's own answer (an empty transfer, or a legitimately
    /// empty file) standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device cannot push the error while it streams the reply, so it records it and queues
    /// it at the next command it receives on the same transport, before running that command.
    /// It records it before it sends the EOF marker, so once Core has seen the marker, the first
    /// <c>SYSTem:ERRor?</c> is enough to queue it and read it back. It is read to empty rather
    /// than once, so nothing is left behind to make the next connect fail (#898).
    /// </para>
    /// <para>
    /// What it reads is blamed on the GET only when <paramref name="boundaryConfirmed"/>: the read
    /// before that GET, in the same exclusive block, reached "No error", so nothing older is on
    /// the queue. Otherwise an older entry may be among them, so the queue is still read, to take
    /// the GET's own error off it, but not classified, and the download's own answer stands.
    /// </para>
    /// <para>
    /// Best effort (see <see cref="ReadErrorQueueAsync"/>): if the read itself fails, the
    /// download's own answer is still the right one.
    /// </para>
    /// </remarks>
    /// <exception cref="SdCardFileNotFoundException">The device reported -256 or -257.</exception>
    /// <exception cref="SdCardFilesystemException">The device reported -250.</exception>
    private async Task ThrowIfSdGetOpenFailedAsync(
        string fileName, bool boundaryConfirmed, CancellationToken cancellationToken)
    {
        var drain = await ReadErrorQueueAsync(cancellationToken).ConfigureAwait(false);
        if (!boundaryConfirmed || drain is not { } read)
        {
            return;
        }

        var failure = ClassifySdGetOpenFailure(fileName, read.Entries);
        if (failure != null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Reads the device's SCPI error queue until it reports "No error". Returns what it held and
    /// whether it got that far, or <c>null</c> when the read itself failed.
    /// </summary>
    /// <remarks>
    /// Used on both sides of a v3.9.0+ <c>SD:GET</c> (#959): before it, to leave the queue
    /// empty, and after an empty reply, to find out why. Best effort. If the read fails (the link
    /// dropped, the device stopped answering), the download goes on to its own answer, so that
    /// failure is dropped. Cancellation is not: it belongs to the caller or to the download
    /// deadline.
    /// </remarks>
    private async Task<ErrorQueueDrainResult?> ReadErrorQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _host.DrainErrorQueueUntilEmptyAsync(SdGetErrorQueueDrainCap, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the error queue as the boundary ahead of a v3.9.0+ <c>SD:GET</c>, and returns
    /// whether it reached "No error". False when the read failed or stopped early.
    /// </summary>
    private async Task<bool> ReadErrorQueueToEmptyAsync(CancellationToken cancellationToken)
    {
        var drain = await ReadErrorQueueAsync(cancellationToken).ConfigureAwait(false);
        return drain?.ReachedEmpty == true;
    }

    /// <summary>
    /// Picks the open failure out of the error-queue entries read after an empty <c>SD:GET</c>,
    /// or returns <c>null</c> when none of them is one.
    /// </summary>
    /// <remarks>
    /// The queue is read empty before the GET, so this normally sees at most one entry. If more
    /// than one arrives anyway, the newest matching entry wins, because the device queues its
    /// open failure at the latest command boundary. Entries with other codes are not about this
    /// file and do not change the answer. They are still carried in the exception's
    /// <see cref="SdCardOperationException.RawDeviceResponse"/>.
    /// </remarks>
    internal static SdCardFilesystemException? ClassifySdGetOpenFailure(
        string fileName, IReadOnlyList<string> queuedErrors)
    {
        for (var i = queuedErrors.Count - 1; i >= 0; i--)
        {
            var entry = queuedErrors[i];
            if (!ScpiResponseClassifier.TryParseSystemErrorReplyCode(entry, out var code))
            {
                continue;
            }

            switch (code)
            {
                case ScpiFileNameNotFound:
                case ScpiFileNameError:
                    return new SdCardFileNotFoundException(fileName, code, queuedErrors, entry);
                case ScpiMassStorageError:
                    return new SdCardFilesystemException(queuedErrors, entry, entry);
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads a file from the device's SD card, over USB or over WiFi/TCP, to a temporary file.
    /// </summary>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata about the downloaded file, including the local file path.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">Thrown over a WiFi/TCP transport when the firmware predates SD-over-WiFi file transfer.</exception>
    /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    internal async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
        string fileName,
        IProgress<SdCardTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        var tempPath = Path.Combine(Path.GetTempPath(), $"daqifi_{Guid.NewGuid():N}{ext}");
        try
        {
            var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true);

            await using (fileStream.ConfigureAwait(false))
            {
                var result = await DownloadSdCardFileAsync(fileName, fileStream, progress, cancellationToken)
                    .ConfigureAwait(false);

                return result with { FilePath = tempPath };
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            throw;
        }
    }


    /// <summary>
    /// The part of <paramref name="budget"/> not yet consumed, floored at zero (a negative
    /// timeout is not a legal <see cref="CancellationTokenSource"/> delay).
    /// </summary>
    private static TimeSpan RemainingBudget(TimeSpan budget, TimeSpan elapsed)
    {
        var remaining = budget - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// The instant the download is given up on regardless of what it is doing. It sits just
    /// past the cooperative <paramref name="budget"/> so that a transfer which IS observing
    /// its token still fails through the receiver's own timeout — which reports how many
    /// bytes arrived — and the hard deadline only decides the case where it is not.
    /// </summary>
    private static TimeSpan HardDeadlineFor(TimeSpan budget)
    {
        var graceMs = Math.Clamp(budget.TotalMilliseconds * 0.1, 100, 5000);
        return budget + TimeSpan.FromMilliseconds(graceMs);
    }

    /// <summary>
    /// Runs an SD download on a worker task and races it against a hard deadline, so neither
    /// the deadline nor the caller's cancellation depends on the transfer being somewhere it
    /// can observe a token (#399). On expiry the worker is abandoned rather than awaited.
    /// </summary>
    /// <param name="operation">The transfer. Receives a token cancelled by caller cancellation or the deadline, whichever comes first.</param>
    /// <param name="budget">The cooperative budget; the hard deadline is <see cref="HardDeadlineFor"/> of it.</param>
    /// <param name="fileName">Used only in the <see cref="TimeoutException"/> message.</param>
    /// <param name="cancellationToken">The caller's token, observed by the race itself and not only by the worker.</param>
    /// <param name="onWorkerAbandoned">
    /// Invoked, before this method throws, when the worker is given up on while still running.
    /// Lets the caller skip any cleanup that would touch the transport the abandoned worker
    /// still owns.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a previous download still owns <see cref="_sdDownloadGate"/> — it is either
    /// genuinely in flight or was abandoned and is still parked on the transport.
    /// </exception>
    private async Task RunWithHardDeadlineAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan budget,
        string fileName,
        CancellationToken cancellationToken,
        Action? onWorkerAbandoned = null)
    {
        // Checked before taking the gate so a cancelled caller neither acquires it nor gets an
        // answer about some other transfer.
        cancellationToken.ThrowIfCancellationRequested();

        // Fail fast rather than becoming a second reader on a stream an abandoned transfer
        // still holds. Wait(0) never blocks: this either takes the gate or reports the state.
        if (!_sdDownloadGate.Wait(0))
        {
            // Cancellation wins when it raced the gate check — the same precedence the abandon
            // path below applies. The caller asked to stop; that is a truer answer than a
            // report about a different download.
            cancellationToken.ThrowIfCancellationRequested();

            throw new InvalidOperationException(
                "A previous SD card download is still in flight, or was abandoned after timing out and " +
                "is still parked on the transport. Reconnect the device before retrying.");
        }

        // Released exactly once, by whichever path is last to be done with the worker: the
        // finally below in the normal case, or the abandon-path continuation when the worker
        // finally unwinds. Interlocked because a worker that completes right at the deadline
        // boundary can reach both.
        var gateReleased = 0;
        void ReleaseGate()
        {
            if (Interlocked.Exchange(ref gateReleased, 1) != 0)
            {
                return;
            }

            try
            {
                _sdDownloadGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The device was disposed while a transfer was still abandoned. Benign
                // teardown, and this can run from a discarded continuation — never throw.
            }
        }

        var hardDeadline = HardDeadlineFor(budget);

        // hardDeadlineCts runs on its own timer, independent of the Task.Delay race below, so
        // it still reaches the worker if the worker only returns long after the race was
        // decided. linkedCts is what the worker observes: caller cancellation OR the deadline.
        var hardDeadlineCts = new CancellationTokenSource(hardDeadline, Clock);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardDeadlineCts.Token);

        // Stops the racing delay the moment the outcome is decided — without it, a download
        // that finishes in a second would leave a 30-minute timer registered behind it.
        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // LongRunning (a dedicated thread, not a pooled one): the transfer's synchronous
        // prefix — the consumer stop-and-join, PrepareSdInterface's blocking writes — otherwise
        // runs on the CALLING thread up to the first await, which on a UI thread means a
        // wedged device freezes the window, and which would put that prefix outside the very
        // deadline it needs to be inside. A pooled Task.Run would also tie up a worker for the
        // transfer's full blocking duration. Pass CancellationToken.None to StartNew itself:
        // the worker's own token still cancels its waits, and "cancelled before start" must
        // not surface as an operation fault. (Mirrors WifiBridgeActivator, #294/#295/#326.)
        var workerTask = Task.Factory.StartNew(
            () => operation(linkedCts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        try
        {
            var winner = await Task.WhenAny(
                workerTask,
                Task.Delay(hardDeadline, Clock, raceCts.Token)).ConfigureAwait(false);

            // Only abandon when the worker is genuinely still running: WhenAny can hand back
            // the delay even though the worker completed at that same boundary, and awaiting
            // it below honors that result instead of discarding it.
            if (winner != workerTask && !workerTask.IsCompleted)
            {
                // Tell the caller before unwinding: the worker keeps running and keeps the
                // transport, so any cleanup that would write to it has to be skipped.
                onWorkerAbandoned?.Invoke();

                // Cancel explicitly instead of relying on the deadline timer having fired: the
                // delay above and hardDeadlineCts are two separate timers of the same duration,
                // so the delay can win by a hair and leave a late-returning worker running one
                // more state-changing step after the caller already threw. Idempotent.
                hardDeadlineCts.Cancel();

                // The worker may be parked in native serial I/O that no token can interrupt, so
                // it is ABANDONED, not awaited — waiting for it is the hang being bounded here.
                // Observe its eventual fault so it cannot resurface as an UnobservedTaskException,
                // and dispose the sources only once it is done with them (disposing early would
                // turn its pending waits into ObjectDisposedException instead of cancellation).
                _ = workerTask.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        linkedCts.Dispose();
                        hardDeadlineCts.Dispose();

                        // Only now is the transport genuinely free for another download.
                        ReleaseGate();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                // Prefer surfacing caller cancellation over a generic timeout when both raced.
                cancellationToken.ThrowIfCancellationRequested();

                throw new TimeoutException(
                    $"SD card download of '{fileName}' did not complete within " +
                    $"{hardDeadline.TotalSeconds:0.#}s and was abandoned. The device's SD " +
                    "subsystem is not responding; reconnect (or power-cycle) before retrying.");
            }

            // Propagate success or the transfer's own exception unchanged.
            await workerTask.ConfigureAwait(false);
        }
        finally
        {
            raceCts.Cancel();
            raceCts.Dispose();

            // The abandon path hands disposal and the gate to its continuation instead; do it
            // here only when the worker actually finished (the common, non-hung case).
            if (workerTask.IsCompleted)
            {
                linkedCts.Dispose();
                hardDeadlineCts.Dispose();
                ReleaseGate();
            }
        }
    }


    /// <summary>
    /// Harmony <c>SYS_FS_ERROR</c> codes meaning "that path is not on the card":
    /// <c>SYS_FS_ERROR_NO_FILE = 4</c> and <c>SYS_FS_ERROR_NO_PATH = 5</c>. The
    /// enum runs sequentially from <c>SYS_FS_ERROR_OK = 0</c> — see the
    /// firmware's <c>config/default/system/fs/sys_fs.h</c>.
    /// </summary>
    private static readonly int[] DirectoryNotFoundFsErrors = { 4, 5 };

    /// <summary>
    /// Reads the <c>N</c> out of a <c>"[Error:N]Failed to open directory [path]"</c>
    /// line and reports whether it is a not-found code. An unparsable or
    /// unrecognised code answers false, so anything unfamiliar keeps the broader
    /// <see cref="SdCardFilesystemException"/> rather than being narrowed on a guess.
    /// </summary>
    private static bool IsDirectoryNotFound(string deviceMessage)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            deviceMessage, @"^\[Error:(\d+)\]");
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, out var code)
            && System.Array.IndexOf(DirectoryNotFoundFsErrors, code) >= 0;
    }

    /// <summary>
    /// Pulls the path out of the trailing <c>[path]</c> of a firmware directory
    /// error line, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Searches only AFTER the leading <c>[Error:N]</c>. Taking the last bracket
    /// pair in the whole line looks equivalent and is not: on the older phrasing
    /// <c>"[Error:5]Failed to open directory /Daqifi"</c>, which has no bracketed
    /// path at all, the last pair IS the error prefix and the path comes back as
    /// <c>"Error:5"</c>. Caught by the test that pins that phrasing.
    /// </remarks>
    private static string? ExtractBracketedPath(string deviceMessage)
    {
        var prefixEnd = deviceMessage.IndexOf(']');
        var searchFrom = prefixEnd >= 0 ? prefixEnd + 1 : 0;
        if (searchFrom >= deviceMessage.Length)
        {
            return null;
        }

        var close = deviceMessage.LastIndexOf(']');
        if (close < searchFrom)
        {
            return null;
        }

        var open = deviceMessage.LastIndexOf('[', close - 1);
        if (open < searchFrom)
        {
            return null;
        }

        var path = deviceMessage.Substring(open + 1, close - open - 1).Trim();
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// Inspects the final response from a <c>SYSTem:STORage:SD:LISt?</c> exchange
    /// and returns the typed <see cref="SdCardOperationException"/> for a real failure the
    /// device reported (no SD card, filesystem error, generic SCPI error), or null when
    /// there is none. Returned rather than thrown so <see cref="ClassifySdListReply"/> can
    /// rank it and each caller can decide what to do with it.
    /// If any non-error/non-empty line is present, callers proceed to parse — even
    /// if SCPI error lines are interleaved — so a successful directory listing is
    /// never masked by stray transient errors.
    /// </summary>
    private static SdCardOperationException? GetSdCardListError(IReadOnlyList<string> lines)
    {
        // LastScpiError must only carry a real SCPI-formatted error so callers
        // can rely on its shape. Firmware status text ("Error !! ...") is
        // surfaced via the exception's Message and RawDeviceResponse instead.
        var lastScpiError = ScpiResponseClassifier.GetLastScpiErrorLine(lines);

        // Specific firmware-emitted error markers take precedence over generic
        // content/error checks. They're plain text (not SCPI-shaped), so a
        // simple "is there any content line?" check would otherwise miss them
        // and pass garbage to the parser.
        if (lines.Any(l => l.IndexOf(NoSdCardMarker, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return new SdCardNotPresentException(lines, lastScpiError);
        }

        var filesystemErrorLine = lines.FirstOrDefault(l =>
            l.IndexOf(DirectoryOpenFailureMarker, StringComparison.OrdinalIgnoreCase) >= 0);
        if (filesystemErrorLine != null)
        {
            var trimmed = filesystemErrorLine.Trim();

            // "The directory is not there" is a normal state, not a fault, and
            // it is worth telling apart: a freshly formatted card has no log
            // directory until the first capture writes one, so a caller that
            // renders every filesystem error identically shows a scary message
            // for "you have not logged anything yet". Firmware #798 is what
            // makes this observable at all -- before it, listing a directory
            // that did not exist created it and reported it empty.
            if (IsDirectoryNotFound(trimmed))
            {
                return new SdCardDirectoryNotFoundException(
                    lines, lastScpiError, trimmed, ExtractBracketedPath(trimmed));
            }

            return new SdCardFilesystemException(lines, lastScpiError, trimmed);
        }

        // If any line looks like a real result (non-empty, not an error or
        // firmware status line), hand off to the parser. Stray interleaved
        // error lines are still parsed away by SdCardFileListParser.
        //
        // The end-of-listing marker is framing, not a result. Counting it as
        // content would let a stale marker -- one left in the buffer by an
        // earlier timed-out exchange, which is exactly what
        // TrySplitAtSdListTerminator warns about -- vouch for a reply whose
        // only other line is this request's SCPI error, and the caller would
        // be handed a confidently empty card.
        var hasContentLine = lines.Any(line =>
            !string.IsNullOrWhiteSpace(line)
            && !ScpiResponseClassifier.IsErrorResponseLine(line)
            && !SdCardFileListParser.IsListEndMarker(line, out _));
        if (hasContentLine)
        {
            return null;
        }

        if (lastScpiError != null)
        {
            return new SdCardOperationException(
                "The SD card list operation failed: " + lastScpiError,
                lines,
                lastScpiError);
        }

        // Defensive fallback: firmware status text ("Error !! ...") with no
        // SCPI error and no recognized marker. Shouldn't happen for known
        // firmware paths, but surfacing it as a typed exception is far
        // better than silently returning an empty list.
        var nonResultLine = lines.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l) && ScpiResponseClassifier.IsErrorResponseLine(l))?.Trim();
        if (nonResultLine != null)
        {
            return new SdCardOperationException(
                "The SD card list operation failed: " + nonResultLine,
                lines,
                lastScpiError: null);
        }

        // No error lines and no content lines — empty directory. Caller continues.
        // Safe to treat as empty rather than as a lost reply: GetSdCardFilesAsync only reaches
        // this point once the device has answered the end-of-listing terminator (#396).
        return null;
    }
}
