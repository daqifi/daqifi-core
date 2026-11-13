using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Defines operations for interacting with a device's SD card for data logging and file management.
/// </summary>
public interface ISdCardOperations
{
    /// <summary>
    /// Raised before SD-card logging starts when a low-free-space condition is detected by
    /// <see cref="CheckSdCardSpaceAsync"/> — either the card is nearly full or the planned capture
    /// will not fit. The warning is advisory: it does not block logging. Subscribers can surface a
    /// confirmable prompt and let the user proceed.
    /// </summary>
    event EventHandler<LowSdSpaceWarningEventArgs> LowSdSpaceWarning;

    /// <summary>
    /// Gets a value indicating whether the device is currently logging data to the SD card.
    /// </summary>
    /// <remarks>
    /// It goes false when the session ends, by <see cref="StopSdCardLoggingAsync"/>,
    /// <see cref="IStreamingDevice.StopStreaming"/>, or a stop command sent any other way,
    /// including the one <see cref="DaqifiDevice.InitializeAsync"/> sends. It also goes false when
    /// the connection ends, by a disconnect or a lost link: this object then knows nothing of what
    /// the device is doing, although nothing has told the device to stop.
    /// </remarks>
    bool IsLoggingToSdCard { get; }

    /// <summary>
    /// Gets the most recently retrieved list of files on the SD card.
    /// </summary>
    IReadOnlyList<SdCardFileInfo> SdCardFiles { get; }

    /// <summary>
    /// Retrieves the list of files stored on the device's SD card.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation, containing the list of files.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">
    /// Thrown when a logging session is running (<see cref="IsLoggingToSdCard"/>). The device
    /// cannot serve a listing from a card it is writing to, and taking the card off streaming to
    /// read the directory would end the recording, so the listing refuses instead of changing the
    /// session. Stop the session first (closes #851).
    /// </exception>
    /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
    /// <exception cref="SdCardFilesystemException">
    /// Thrown when the SD card filesystem cannot satisfy the request (e.g. corrupt card,
    /// unreadable directory). A log directory that is simply not on the card is not one of
    /// these: a new card has no log directory until the first capture writes one, so the listing
    /// reports it as an empty card and returns an empty list (closes #730). An absent card still
    /// throws <see cref="SdCardNotPresentException"/>, and a directory the device could not read
    /// for any other reason still throws this.
    /// </exception>
    /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error that did not match a more specific condition. An empty directory returns an empty list rather than throwing.</exception>
    /// <exception cref="SdCardListIncompleteException">
    /// Thrown when the listing cannot be trusted as complete. Either the device did not answer
    /// — the <c>SYSTem:ERRor?</c> terminator appended to the listing query never came back — or
    /// it did answer and its own end-of-listing marker reported the walk as incomplete or failed
    /// (<c>__END_OF_LIST__</c> with <c>INCOMPLETE</c>, <c>FAILED</c>, or any status other than
    /// <c>OK</c>; firmware #794), or — on firmware that emits that marker, v3.8.0 and above —
    /// the reply carried no marker at all with nothing to explain its absence, which is a walk
    /// that never reached its own end (closes #887). An empty result therefore always means an
    /// empty card — never an unreachable device or a walk the device itself called short — so
    /// callers can render it as such (closes #396).
    /// <para>
    /// One case stays outside what Core can see: firmware that answers
    /// <c>__END_OF_LIST__ OK</c> with no entries while the directory was in fact unreadable is
    /// claiming a finished walk, and Core has no second source to contradict it. That is a
    /// firmware-side report of "I could not look" as "there is nothing there", tracked as a
    /// companion to <c>daqifi-nyquist-firmware#925</c>; below v3.8.0, where there is no marker,
    /// Core cannot distinguish a short walk from a complete one at all and an empty listing is
    /// taken at face value.
    /// </para>
    /// </exception>
    Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the free and total byte counts of the device's SD card.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation, containing the SD card storage info.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
    /// <exception cref="Daqifi.Core.Device.FeatureNotSupportedException">Thrown when the device's firmware does not recognize the storage query (SCPI -113 "Undefined header"), typically because it predates the minimum supported firmware.</exception>
    /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error or an unparseable response.</exception>
    Task<SdCardStorageInfo> GetSdCardStorageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the SD card's free space and evaluates it against the optional planned capture, raising
    /// <see cref="LowSdSpaceWarning"/> when the card is nearly full or the capture will not fit.
    /// Intended to be called immediately before <see cref="StartSdCardLoggingAsync"/> as a pre-flight check.
    /// </summary>
    /// <param name="plannedCapture">
    /// An optional estimate of the upcoming capture. When supplied, the result includes a "won't fit"
    /// check and a truncation ETA. When omitted, only the "nearly full" threshold is applied.
    /// </param>
    /// <param name="minimumFreeBytes">
    /// The "nearly full" threshold in bytes. Defaults to <see cref="SdCardSpaceCheck.DefaultMinimumFreeBytes"/> (100 MB).
    /// </param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// A task that resolves to the evaluated <see cref="SdCardSpaceCheckResult"/>. The check never blocks
    /// logging; callers decide whether to proceed based on <see cref="SdCardSpaceCheckResult.ShouldWarn"/>.
    /// </returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
    /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error or an unparseable response.</exception>
    Task<SdCardSpaceCheckResult> CheckSdCardSpaceAsync(
        SdCardCaptureEstimate? plannedCapture = null,
        long minimumFreeBytes = SdCardSpaceCheck.DefaultMinimumFreeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the firmware-enforced minimum free-space floor on the SD card. When set, the firmware refuses
    /// to start an SD-output stream once free space would drop below the floor, surfacing a
    /// <c>-200 "Execution error"</c> rather than silently truncating. This is the optional hand-off to the
    /// firmware gate; the client-side <see cref="LowSdSpaceWarning"/> remains the primary UX surface.
    /// </summary>
    /// <param name="bytes">The minimum free space to keep available, in bytes. Use 0 to disable the gate.</param>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown when <paramref name="bytes"/> is negative.</exception>
    void SetSdCardMinimumFreeSpace(long bytes);

    /// <summary>
    /// Turns CRC-protected SD card writes on or off (firmware v3.9.0 and later).
    /// </summary>
    /// <remarks>
    /// <para>
    /// With protection on, the card checks a CRC on every block and the device writes each block
    /// on its own. A block the card picked up from other traffic on the shared SPI bus then fails
    /// its check and is refused, instead of being written over whatever sector it landed on. That
    /// is the failure that corrupted the card's FAT in <c>daqifi-nyquist-firmware#1217</c>. Turn
    /// it on when the integrity of a log matters more than how fast it can be written.
    /// </para>
    /// <para>
    /// <b>It costs throughput.</b> It takes roughly two thirds off the SD write speed, leaving
    /// about a third, so a sample rate that logs cleanly unprotected may not keep up protected. It
    /// also makes a WiFi connection much less responsive while the device is logging to the card
    /// (<c>daqifi-nyquist-firmware#1234</c>): commands sent over WiFi during an SD log can stall.
    /// Both are why the device ships with it off.
    /// </para>
    /// <para>
    /// The device accepts the change only while SD storage is off and idle. This call refuses
    /// while this object is running an SD log rather than stopping it: stop the log with
    /// <see cref="StopSdCardLoggingAsync"/> first. A live stream that is not going to the card is
    /// paused for the command and resumed afterwards, as the other SD operations do.
    /// </para>
    /// <para>
    /// A change takes a second or two. The device applies it by re-initializing the card, which
    /// cannot be mounted until the device picks it up again at its next card check. This call
    /// confirms a card is ready by mounting it (the storage-space query) and then reads back the
    /// mode the card came up in, so an SD log or file operation started right after it finds the
    /// card in that mode. That check runs even when the value did not change, so an empty slot
    /// reports <see cref="SdCardCrcProtectionResult.Pending"/> rather than
    /// <see cref="SdCardCrcProtectionResult.Applied"/>. Calls on one device run one at a time,
    /// each through to its confirmation, and an SD log started during a change waits for it.
    /// </para>
    /// <para>
    /// <b>The device forgets the setting every time it boots</b>, and Core does not re-apply it.
    /// Set it again after each connect, and after a reboot. Read it back with
    /// <see cref="GetSdCardCrcProtectionAsync"/> before a capture whose integrity matters.
    /// </para>
    /// <para>
    /// Added with a default body, as <see cref="Firmware.IFirmwareUpdateService"/>'s targeted
    /// overloads were, so an implementation of this interface written before it existed still
    /// compiles and loads; that default throws <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    /// <param name="enabled"><c>true</c> to turn protection on; <c>false</c> for the fast default.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// <see cref="SdCardCrcProtectionResult.Applied"/> once a card is mounted in the new mode,
    /// or <see cref="SdCardCrcProtectionResult.Pending"/> when the device stored the change but no
    /// card is ready with it yet. <see cref="SdCardCrcProtectionResult.Pending"/> lists the
    /// causes and what each blocks.
    /// </returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device has no SD card slot or its firmware predates v3.9.0
    /// (<see cref="DeviceFeature.SdCardCrcProtection"/>). Nothing is sent.
    /// </exception>
    /// <exception cref="SdCardBusyException">
    /// Thrown when the SD card is in use, so the setting was not changed. Either this object is
    /// running an SD log (<see cref="IsLoggingToSdCard"/>), in which case nothing is sent, or the
    /// device refused with SCPI <c>-221</c> (settings conflict) because its SD storage is enabled
    /// or still busy — for example a format that is still running, or SD storage turned on by
    /// another connection. Let it finish or turn SD storage off, then try again.
    /// </exception>
    /// <exception cref="SdCardOperationException">
    /// Thrown when the device answered with any other SCPI error, or did not answer at all; in
    /// the second case whether the setting changed is unknown, and
    /// <see cref="GetSdCardCrcProtectionAsync"/> reads what the device holds. Also thrown when
    /// the card came back in the other mode, because something other than this call (another
    /// connection, a raw command) changed the setting while it waited.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown by an implementation written before this member existed, which inherits the default
    /// body. <see cref="DaqifiStreamingDevice"/> implements it.
    /// </exception>
    Task<SdCardCrcProtectionResult> SetSdCardCrcProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This ISdCardOperations implementation does not support CRC-protected SD writes.");

    /// <summary>
    /// Reads whether CRC-protected SD card writes are on (firmware v3.9.0 and later). See
    /// <see cref="SetSdCardCrcProtectionAsync"/> for what the setting does and what it costs.
    /// </summary>
    /// <remarks>
    /// This asks the device rather than remembering the last value set, because the device
    /// forgets the setting on every boot: a device that rebooted since it was set reports
    /// <c>false</c> here. The answer is the value the device holds, which may still be waiting to
    /// be applied (<see cref="SdCardCrcProtectionResult.Pending"/>). Safe to call during an SD log.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns><c>true</c> when protection is on; otherwise <c>false</c>.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device has no SD card slot or its firmware predates v3.9.0
    /// (<see cref="DeviceFeature.SdCardCrcProtection"/>). Nothing is sent.
    /// </exception>
    /// <exception cref="SdCardOperationException">
    /// Thrown when the device answered with a SCPI error or with no readable <c>0</c> or
    /// <c>1</c>. A live stream running at the time can bury the answer among stream data.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown by an implementation written before this member existed, which inherits the default
    /// body. <see cref="DaqifiStreamingDevice"/> implements it.
    /// </exception>
    Task<bool> GetSdCardCrcProtectionAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This ISdCardOperations implementation does not support CRC-protected SD writes.");

    /// <summary>
    /// Starts logging data to the SD card.
    /// </summary>
    /// <remarks>
    /// This method does not perform a free-space pre-flight. To warn the user about a near-full card
    /// before committing to a capture, call <see cref="CheckSdCardSpaceAsync"/> first and let the user
    /// decide whether to proceed.
    /// </remarks>
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
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// To learn the effective on-card file name (especially when it was auto-generated), call
    /// <see cref="StartSdCardLoggingSessionAsync"/> instead, which returns an
    /// <see cref="SdCardLoggingSession"/>.
    /// </remarks>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts logging data to the SD card and returns the effective session details. Behaves
    /// exactly like <see cref="StartSdCardLoggingAsync"/> but resolves to an
    /// <see cref="SdCardLoggingSession"/> carrying the effective on-card file name (supplied or
    /// auto-generated) and the logging format, so callers can report, download, or later delete
    /// the log without re-deriving Core's naming convention.
    /// </summary>
    /// <param name="fileName">
    /// The name of the log file. If null or empty, a timestamped name is generated automatically
    /// using the pattern "log_YYYYMMDD_HHMMSS" with a format-appropriate extension.
    /// </param>
    /// <param name="channelMask">
    /// Optional decimal bitmask string to enable specific ADC channels (e.g. "3" enables channels 0 and 1).
    /// If null or empty, the current device channel configuration is used.
    /// </param>
    /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// A task that resolves to an <see cref="SdCardLoggingSession"/> with the effective on-card
    /// file name and logging format.
    /// </returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops logging data to the SD card and returns the device to the state a live stream
    /// expects: the stream encoding back to protobuf, the stream interface back to USB, and the
    /// LAN interface back on.
    /// </summary>
    /// <remarks>
    /// The encoding is one global firmware setting shared by every interface, so a JSON or CSV
    /// session changed it for live streaming too. This call sets it back to protobuf, the format
    /// the library establishes on every connection and the only one its frame decoder reads. A
    /// caller that chose a different live format by hand must set it again (closes #851).
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file from the SD card.
    /// </summary>
    /// <param name="fileName">The name of the file to delete.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats the entire SD card, erasing all data.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    Task FormatSdCardAsync(CancellationToken cancellationToken = default);

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
    /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="SdCardEmptyTransferException">
    /// Thrown when the device serves a marker-only (0-byte) transfer for a file the last
    /// listing reported as non-empty (or whose listed size is unknown). A file the listing
    /// reports as 0 bytes downloads successfully as a legitimate empty file.
    /// </exception>
    /// <exception cref="SdCardTransferErrorException">
    /// Thrown when the device ends the transfer with the transfer-error marker it gained in
    /// firmware v3.7.3 — it failed to read the rest of the file off the card. What was written
    /// to <paramref name="destinationStream"/> is genuine but partial file content.
    /// </exception>
    /// <exception cref="SdCardTruncatedTransferException">
    /// Thrown when the transfer ends at the end-of-file marker with fewer bytes than the last
    /// listing reported for the file — the device served a short reply, such as a SCPI error
    /// line, in place of the file. Anything already written to
    /// <paramref name="destinationStream"/> is not the file and must be discarded. The check
    /// needs a size to compare against, so call <see cref="GetSdCardFilesAsync"/> before
    /// downloading; with no listing the download cannot tell a short reply from a small file.
    /// </exception>
    /// <exception cref="SdCardTransferStalledException">
    /// Thrown when the transfer stops making progress before the end-of-file marker arrives;
    /// <see cref="SdCardTransferStalledException.Reason"/> distinguishes a stalled read from a
    /// closed transport and from an elapsed transfer deadline.
    /// </exception>
    /// <exception cref="System.TimeoutException">
    /// Thrown when the transfer does not finish within the implementation's download deadline.
    /// The deadline is enforced by the download itself, so it holds even when the transfer is
    /// parked in a call that cannot observe a cancellation token; the in-flight transfer is
    /// then abandoned rather than awaited (#399). An abandoned transfer still owns the
    /// transport, so the interface it switched is left as-is rather than restored over the top
    /// of it — reconnect before using the device again.
    /// </exception>
    Task<SdCardDownloadResult> DownloadSdCardFileAsync(
        string fileName,
        Stream destinationStream,
        IProgress<SdCardTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file from the device's SD card, over USB or over WiFi/TCP, to a temporary file.
    /// </summary>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata about the downloaded file, including the local <see cref="SdCardDownloadResult.FilePath"/>.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">Thrown over a WiFi/TCP transport when the firmware predates SD-over-WiFi file transfer.</exception>
    /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
    /// <exception cref="SdCardBusyException">Thrown when the device is currently logging to SD card.</exception>
    /// <exception cref="System.TimeoutException">
    /// Thrown when the transfer does not finish within the implementation's download deadline
    /// (see the <see cref="Stream"/> overload above); the temporary file is removed.
    /// </exception>
    Task<SdCardDownloadResult> DownloadSdCardFileAsync(
        string fileName,
        IProgress<SdCardTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
