using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
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
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
        /// <exception cref="SdCardFilesystemException">Thrown when the SD card filesystem cannot satisfy the request (e.g. corrupt card, unreadable directory).</exception>
        /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error that did not match a more specific condition. An empty directory returns an empty list rather than throwing.</exception>
        Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves the free and total byte counts of the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the SD card storage info.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
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
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
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
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when <paramref name="bytes"/> is negative.</exception>
        void SetSdCardMinimumFreeSpace(long bytes);

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
        /// (.bin for Protobuf, .json for JSON, .dat for TestData).
        /// </param>
        /// <param name="channelMask">
        /// Optional decimal bitmask string to enable specific ADC channels (e.g. "3" enables channels 0 and 1).
        /// The firmware parses this as a decimal integer where each bit enables a channel.
        /// If null or empty, the current device channel configuration is used.
        /// </param>
        /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops logging data to the SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a file from the SD card.
        /// </summary>
        /// <param name="fileName">The name of the file to delete.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Formats the entire SD card, erasing all data.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        Task FormatSdCardAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a file from the device's SD card over USB.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="destinationStream">The stream to write file contents to.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
        /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            Stream destinationStream,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a file from the device's SD card over USB to a temporary file.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file, including the local <see cref="SdCardDownloadResult.FilePath"/>.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
        /// <exception cref="System.ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
