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
        Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts logging data to the SD card.
        /// </summary>
        /// <param name="fileName">
        /// The name of the log file. If null or empty, a timestamped name is generated automatically
        /// using the pattern "log_YYYYMMDD_HHMMSS" with an extension matching <paramref name="format"/>
        /// (.bin for Protobuf, .json for JSON, .dat for TestData).
        /// </param>
        /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task StartSdCardLoggingAsync(string? fileName = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default);

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
