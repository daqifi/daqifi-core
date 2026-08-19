using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
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
    /// <see cref="IDeviceOperationHost"/>, which keeps the device's virtual members (and therefore
    /// any subclass override of them) in the path.
    /// </remarks>
    internal sealed class SdCardOperations
    {
        /// <summary>
        /// The delay in milliseconds to wait after switching between LAN and SD card interfaces.
        /// The SD card and LAN share the SPI bus, so a settle period is needed for the device
        /// firmware to complete the interface switch before sending further commands.
        /// </summary>
        private const int SD_INTERFACE_SETTLE_DELAY_MS = 100;

        /// <summary>
        /// Maximum number of retry attempts for SD card list operations that receive transient
        /// SCPI errors (e.g., -200 Execution error) due to interface-switch timing.
        /// </summary>
        private const int SD_LIST_MAX_RETRIES = 1;

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

        private readonly IDeviceOperationHost _host;

        private bool _isLoggingToSdCard;
        private IReadOnlyList<SdCardFileInfo> _sdCardFiles = Array.Empty<SdCardFileInfo>();

        /// <summary>
        /// Admits one SD download at a time. A download that hits its deadline is ABANDONED, not
        /// stopped — its worker can still be parked in native I/O holding the transport stream —
        /// so the gate is released only when that worker actually finishes, however long that
        /// takes. Without it, a caller retrying against a device that stays wedged (an "import
        /// all" loop, say) would start a second reader on the same stream, which is the framing
        /// corruption <see cref="DaqifiDevice"/> already refuses to risk when restarting the
        /// protobuf consumer, and would stack another permanently blocked thread each time (#399).
        /// </summary>
        /// <remarks>
        /// Deliberately not disposed: we only ever call <see cref="SemaphoreSlim.Wait(int)"/> and
        /// <see cref="SemaphoreSlim.Release()"/>, never <see cref="SemaphoreSlim.AvailableWaitHandle"/>,
        /// so there is no handle to release — and an abandoned worker may release this long after
        /// the device is disposed, which would otherwise fault a continuation nobody observes.
        /// </remarks>
        private readonly SemaphoreSlim _sdDownloadGate = new(1, 1);

        internal SdCardOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="ISdCardOperations.IsLoggingToSdCard" />
        internal bool IsLoggingToSdCard => _isLoggingToSdCard;

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
        /// Thrown when the listing did not arrive in full — the device never answered, or stopped
        /// answering part-way through. Distinguishing this from a genuinely empty card is the whole
        /// point of the terminator probe described in the remarks (closes #396).
        /// </exception>
        /// <remarks>
        /// <para>
        /// The firmware emits no end-of-listing marker, and for an empty directory it writes nothing
        /// at all, so a lost or truncated reply is byte-for-byte indistinguishable from a healthy
        /// empty card. Core closes that gap by appending a <c>SYSTem:ERRor?</c> query to the same
        /// text exchange: the transport delivers in order and the firmware does not process the
        /// next command until the listing has been handed to the output, so receiving the reply
        /// proves both that the device is answering and that the listing ahead of it is complete.
        /// Its absence means the response is incomplete, and the caller gets an exception instead of
        /// a plausible-looking empty list.
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
        /// </remarks>
        internal async Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
        {
            _host.EnsureConnected();

            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

            IReadOnlyList<string> lines = Array.Empty<string>();
            IReadOnlyList<string> listing = Array.Empty<string>();
            var isComplete = false;

            // Attempt 0 plus SD_LIST_MAX_RETRIES retries. A SCPI error here is often a transient
            // timing issue, and an unterminated response can be a one-off stall, so both are
            // retried once after an additional settle delay before being surfaced.
            for (var attempt = 0; attempt <= SD_LIST_MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);
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
                lines = await _host.ExecuteTextCommandAsync(
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

                isComplete = TrySplitAtSdListTerminator(lines, out listing);

                if (isComplete && !ScpiResponseClassifier.ContainsScpiError(listing))
                {
                    break;
                }
            }

            if (!isComplete)
            {
                throw new SdCardListIncompleteException(lines);
            }

            ThrowIfSdCardListError(listing);

            // The device's own end-of-listing marker (firmware #794), when it sends
            // one. FAILED means the walk could not open the directory at all, so an
            // empty result would report "there is nothing there" for what was really
            // "I could not look" -- the exact confusion the terminator above exists
            // to prevent, one level deeper. Unterminated is the pre-#794 firmware and
            // is not an error: the reply framing already told us the exchange
            // completed, we simply learn nothing more from the device.
            var listingStatus = SdCardFileListParser.GetListingStatus(listing);
            if (listingStatus == SdCardListingStatus.Failed
                || listingStatus == SdCardListingStatus.Incomplete)
            {
                // INCOMPLETE throws for the same reason a missing transport
                // terminator does, a few lines above: the caller is about to
                // cache this list and answer "is my file on the card?" from it.
                // A device-truncated listing and a transport-truncated one are
                // the same class of wrong answer, so they raise the same
                // exception -- a client that handles one already handles this.
                throw new SdCardListIncompleteException(lines);
            }

            var files = SdCardFileListParser.ParseFileList(listing);
            _sdCardFiles = files;
            return files;
        }

        /// <summary>
        /// Prepare phase shared by the SD card text exchanges: switches the shared SPI bus over to
        /// the card and waits for the firmware to complete the switch.
        /// </summary>
        /// <remarks>
        /// Passed as the <c>prepareAsync</c> phase of
        /// <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task})"/>
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
            await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Finalize phase shared by the SD card text exchanges: hands the shared SPI bus back to the
        /// LAN interface. The mirror of <see cref="PrepareSdInterfaceAndSettleAsync"/>.
        /// </summary>
        /// <remarks>
        /// Passed as the <c>finalizeAsync</c> phase of
        /// <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task})"/>
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

            if (_isLoggingToSdCard)
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

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

            // Same prepare/finalize pairing as GetSdCardFilesAsync: the SPI bus switch and its
            // settle wait are the exchange's prepare phase and the LAN restore is its finalize
            // phase, so both halves are held by the one lock acquisition rather than only the
            // switch (#407). The settle wait also moves ahead of the exchange's stale-line
            // boundary instead of blocking a thread inside it.
            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetSdSpace),
                responseTimeoutMs: 3000,
                cancellationToken: cancellationToken,
                prepareAsync: PrepareSdInterfaceAndSettleAsync,
                finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

            // Only retry transient SCPI errors. A "No SD Card Detected" line
            // is non-transient — retrying just delays the typed exception and
            // risks misclassification if the marker isn't repeated on retry.
            if (ScpiResponseClassifier.ContainsScpiError(lines) && !ContainsNoSdCardMarker(lines))
            {
                for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);

                    lines = await _host.ExecuteTextCommandAsync(
                        () => _host.Send(ScpiMessageProducer.GetSdSpace),
                        responseTimeoutMs: 3000,
                        cancellationToken: cancellationToken,
                        prepareAsync: PrepareSdInterfaceAndSettleAsync,
                        finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

                    if (!ScpiResponseClassifier.ContainsScpiError(lines) || ContainsNoSdCardMarker(lines))
                    {
                        break;
                    }
                }
            }

            if (SdCardSpaceParser.TryParseLines(lines, out var storage))
            {
                return storage;
            }

            // Parser failed — translate the firmware response into a typed exception.
            var lastScpiError = lines.LastOrDefault(ScpiResponseClassifier.IsScpiErrorLine)?.Trim();

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
                : $"log_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

            ValidateSdCardFileName(logFileName);

            // SdCardLogFormat integer values map 1:1 to the SYSTem:STReam:FORmat SCPI argument
            var formatCommand = ScpiMessageProducer.SetStreamFormat((int)format);

            // SD card and LAN share the SPI bus on the hardware, so LAN must be
            // disabled before the SD card can be used.
            _host.Send(ScpiMessageProducer.DisableNetworkLan);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            _host.Send(ScpiMessageProducer.EnableStorageSd);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            // Route the data stream to the SD card interface.
            _host.Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.SdCard));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            _host.Send(ScpiMessageProducer.SetSdLoggingFileName(logFileName));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            _host.Send(formatCommand);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(channelMask))
            {
                _host.Send(ScpiMessageProducer.EnableAdcChannels(channelMask));
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            _host.Send(ScpiMessageProducer.StartStreaming(_host.StreamingFrequency));

            _isLoggingToSdCard = true;
            _host.IsStreaming = true;

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

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

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

            _isLoggingToSdCard = false;

            return Task.CompletedTask;
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
        internal async Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            _host.EnsureConnected();

            if (_isLoggingToSdCard)
            {
                throw new SdCardBusyException(Array.Empty<string>());
            }

            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

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
                // arrives, reads as Unterminated (which by design does not
                // throw) and slips past the guard below to overwrite the cache
                // with a partial list. The guard can only judge a marker it
                // was given time to receive.
                completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                cancellationToken: cancellationToken,
                prepareAsync: PrepareSdInterfaceAndSettleAsync,
                finalizeAsync: RestoreLanInterfaceAsync).ConfigureAwait(false);

            if (ScpiResponseClassifier.ContainsScpiError(lines))
            {
                for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);

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

            // Prove the EXCHANGE finished before judging what it contained. A
            // reply cut short by the completion window loses the device's
            // marker too, so it would otherwise read as Unterminated -- which
            // is legitimate on pre-#796 firmware and therefore cannot be
            // treated as an error on its own. The transport terminator is what
            // separates the two, and the read path has always required it.
            var refreshComplete = TrySplitAtSdListTerminator(lines, out var refreshListing);

            if (!refreshComplete && !ScpiResponseClassifier.ContainsScpiError(lines))
            {
                // An unterminated reply with no error is a one-off stall, and
                // the read path gives that a second attempt for the same
                // reason. Re-LIST only: the DELETE was accepted -- nothing
                // reported otherwise -- so re-sending it would ask the device
                // to remove a file that is already gone and turn a transient
                // stall into a hard error. That is also why this is a separate
                // loop from the error retry above, which re-sends both
                // deliberately because there the delete itself may not have
                // landed.
                for (var retry = 0; retry < SD_LIST_MAX_RETRIES && !refreshComplete; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);

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

                    refreshComplete = TrySplitAtSdListTerminator(lines, out refreshListing)
                                      && !ScpiResponseClassifier.ContainsScpiError(refreshListing);
                }
            }

            // The DELETE's own outcome, judged before anything about the
            // listing. The retry loop above exits either way, so a delete the
            // device refused on BOTH attempts arrives here with its error line
            // intact -- and ThrowIfSdCardListError alone will not catch it: its
            // hasContentLine escape returns silently whenever the reply carries
            // real entries, which is the normal case (you deleted one file, the
            // others are still there). That escape is right for the read path,
            // where a stray error has no bearing on the listing; it is wrong
            // here, where the error line IS the answer to "did the delete
            // happen?". Reproduced: a card holding fileA and fileB, a DELETE
            // refused twice, returned success with fileA still in the cache.
            if (ScpiResponseClassifier.ContainsScpiError(lines))
            {
                var deleteError = lines
                    .LastOrDefault(ScpiResponseClassifier.IsScpiErrorLine)?.Trim();
                throw new SdCardOperationException(
                    "The SD card delete operation failed: "
                        + (deleteError ?? "the device reported an error"),
                    lines,
                    deleteError);
            }

            if (!refreshComplete)
            {
                throw new SdCardListIncompleteException(lines);
            }


            // And the error guard the read path has always applied to its own
            // listing. Without it a delete that FAILED on the device twice --
            // the retry above exhausts without throwing -- came back here with
            // an "**ERROR: -200" line, a valid transport terminator and no
            // device marker, so nothing fired: ParseFileList skips the error
            // line, the cache became an EMPTY list, and the method returned
            // success. The caller was told the delete worked and the card is
            // empty, in the one case where the device had said neither.
            ThrowIfSdCardListError(refreshListing);

            // Same guard as the read path: a listing the device itself called
            // INCOMPLETE or FAILED must not become the cached answer to "what
            // is on the card?". This refresh follows a DELETE, so the cache it
            // overwrites is exactly what a caller consults next to decide
            // whether the file is gone -- and a partial list makes every
            // absence look confirmed. The cache is left untouched rather than
            // replaced with a list we know is short.
            var refreshStatus = SdCardFileListParser.GetListingStatus(refreshListing);
            if (refreshStatus == SdCardListingStatus.Failed
                || refreshStatus == SdCardListingStatus.Incomplete)
            {
                throw new SdCardListIncompleteException(lines);
            }

            _sdCardFiles = SdCardFileListParser.ParseFileList(refreshListing);
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

            if (_isLoggingToSdCard)
            {
                throw new SdCardBusyException(Array.Empty<string>());
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

            _host.Send(ScpiMessageProducer.EnableStorageSd);
            _host.Send(ScpiMessageProducer.FormatSdCard);

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
        /// not be reused for anything else; and the device is left mid-<c>SD:GET</c> with the
        /// protobuf consumer stopped, so reconnecting (or power-cycling, if its SD subsystem is
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

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);
            ArgumentNullException.ThrowIfNull(destinationStream);

            cancellationToken.ThrowIfCancellationRequested();

            if (_isLoggingToSdCard)
            {
                throw new SdCardBusyException(Array.Empty<string>());
            }

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            _host.Send(ScpiMessageProducer.StopStreaming);
            _host.IsStreaming = false;

            var stopwatch = Stopwatch.StartNew();
            long fileSize = 0;
            var budget = _host.SdCardDownloadTimeout;

            // Set when the transfer was given up on and left running (#399/#401). Read only by the
            // restore below, on this same async flow.
            var workerAbandoned = false;

            try
            {
                await RunWithHardDeadlineAsync(async token =>
                {
                    await _host.ExecuteRawCaptureAsync(async (stream, ct) =>
                    {
                        // Prepare SD card interface. Transport-aware: over USB it hands the shared
                        // SPI bus to the card, over WiFi/TCP it leaves the LAN up because the reply
                        // comes back over that very connection (#598/#599).
                        PrepareSdInterface();

                        // Let the interface switch settle before the card is asked for anything —
                        // the same wait the LIST/DELETE/space exchanges take for the same reason.
                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);

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
                        var attempt = 0;
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
                                    timeout: RemainingBudget(budget, stopwatch),
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
                            // discards the stream and retries the download.
                            catch (SdCardEmptyTransferException) when (attempt < SD_LIST_MAX_RETRIES)
                            {
                                attempt++;
                                await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);
                                _host.Send(ScpiMessageProducer.GetSdFile(fileName));
                            }
                        }

                        fileSize = bytesReceived;
                    }, token).ConfigureAwait(false);
                },
                budget,
                fileName,
                cancellationToken,
                onWorkerAbandoned: () => workerAbandoned = true).ConfigureAwait(false);
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
                }
            }

            stopwatch.Stop();
            return new SdCardDownloadResult(fileName, fileSize, stopwatch.Elapsed);
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
        private static TimeSpan RemainingBudget(TimeSpan budget, Stopwatch stopwatch)
        {
            var remaining = budget - stopwatch.Elapsed;
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
            var hardDeadlineCts = new CancellationTokenSource(hardDeadline);
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
                    Task.Delay(hardDeadline, raceCts.Token)).ConfigureAwait(false);

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
        /// Inspects the final response from a <c>SYSTem:STORage:SD:LISt?</c> exchange
        /// and throws a typed <see cref="SdCardOperationException"/> when the device
        /// reported a real failure (no SD card, filesystem error, generic SCPI error).
        /// If any non-error/non-empty line is present, callers proceed to parse — even
        /// if SCPI error lines are interleaved — so a successful directory listing is
        /// never masked by stray transient errors.
        /// </summary>
        private static void ThrowIfSdCardListError(IReadOnlyList<string> lines)
        {
            // LastScpiError must only carry a real SCPI-formatted error so callers
            // can rely on its shape. Firmware status text ("Error !! ...") is
            // surfaced via the exception's Message and RawDeviceResponse instead.
            var lastScpiError = lines.LastOrDefault(ScpiResponseClassifier.IsScpiErrorLine)?.Trim();

            // Specific firmware-emitted error markers take precedence over generic
            // content/error checks. They're plain text (not SCPI-shaped), so a
            // simple "is there any content line?" check would otherwise miss them
            // and pass garbage to the parser.
            if (lines.Any(l => l.IndexOf("No SD Card Detected", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                throw new SdCardNotPresentException(lines, lastScpiError);
            }

            var filesystemErrorLine = lines.FirstOrDefault(l =>
                l.IndexOf("Failed to open directory", StringComparison.OrdinalIgnoreCase) >= 0);
            if (filesystemErrorLine != null)
            {
                throw new SdCardFilesystemException(lines, lastScpiError, filesystemErrorLine.Trim());
            }

            // If any line looks like a real result (non-empty, not an error or
            // firmware status line), hand off to the parser. Stray interleaved
            // error lines are still parsed away by SdCardFileListParser.
            var hasContentLine = lines.Any(line =>
                !string.IsNullOrWhiteSpace(line) && !ScpiResponseClassifier.IsErrorResponseLine(line));
            if (hasContentLine)
            {
                return;
            }

            if (lastScpiError != null)
            {
                throw new SdCardOperationException(
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
                throw new SdCardOperationException(
                    "The SD card list operation failed: " + nonResultLine,
                    lines,
                    lastScpiError: null);
            }

            // No error lines and no content lines — empty directory. Caller continues.
            // Safe to treat as empty rather than as a lost reply: GetSdCardFilesAsync only reaches
            // this point once the device has answered the end-of-listing terminator (#396).
        }

        /// <summary>
        /// Validates an SD card filename to prevent SCPI command injection.
        /// </summary>
        /// <param name="fileName">The filename to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the filename contains invalid characters.</exception>
        private static void ValidateSdCardFileName(string fileName)
        {
            if (fileName.IndexOfAny(new[] { '"', '\n', '\r', ';' }) >= 0)
            {
                throw new ArgumentException(
                    "Filename contains invalid characters. Quotes, newlines, and semicolons are not allowed.",
                    nameof(fileName));
            }
        }
    }
}
