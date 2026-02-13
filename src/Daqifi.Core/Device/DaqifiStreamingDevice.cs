using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that supports data streaming functionality.
    /// Extends the base DaqifiDevice with streaming-specific operations.
    /// </summary>
    public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, INetworkConfigurable, ISdCardOperations
    {
        /// <summary>
        /// The delay in milliseconds to wait for the WiFi module to restart after applying configuration.
        /// </summary>
        private const int WIFI_MODULE_RESTART_DELAY_MS = 2000;

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

        private bool _isLoggingToSdCard;
        private IReadOnlyList<SdCardFileInfo> _sdCardFiles = Array.Empty<SdCardFileInfo>();

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        public bool IsStreaming { get; private set; }

        /// <summary>
        /// Gets or sets the streaming frequency in Hz (samples per second).
        /// </summary>
        public int StreamingFrequency { get; set; }

        /// <summary>
        /// Gets a value indicating whether the device is currently logging data to the SD card.
        /// </summary>
        public bool IsLoggingToSdCard => _isLoggingToSdCard;

        /// <summary>
        /// Gets a value indicating whether the device is connected over USB (serial transport).
        /// SD card file downloads require a USB connection because the SD card and WiFi/LAN share the SPI bus.
        /// </summary>
        public virtual bool IsUsbConnection => Transport is SerialStreamTransport;

        /// <summary>
        /// Gets the most recently retrieved list of files on the SD card.
        /// </summary>
        public IReadOnlyList<SdCardFileInfo> SdCardFiles => _sdCardFiles;

        private readonly NetworkConfiguration _networkConfiguration = new NetworkConfiguration();

        /// <summary>
        /// Gets a copy of the current network configuration.
        /// </summary>
        /// <remarks>
        /// Returns a clone to prevent external modification. Use <see cref="UpdateNetworkConfigurationAsync"/>
        /// to change the device's network configuration.
        /// </remarks>
        public NetworkConfiguration NetworkConfiguration => _networkConfiguration.Clone();

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiStreamingDevice"/> class.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        public DaqifiStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
            StreamingFrequency = 100;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiStreamingDevice"/> class with a transport.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="transport">The transport for device communication.</param>
        public DaqifiStreamingDevice(string name, IStreamTransport transport) : base(name, transport)
        {
            StreamingFrequency = 100;
        }

        /// <summary>
        /// Starts streaming data from the device at the configured frequency.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void StartStreaming()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (IsStreaming) return;

            IsStreaming = true;
            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
        }

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void StopStreaming()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (!IsStreaming) return;

            IsStreaming = false;
            Send(ScpiMessageProducer.StopStreaming);
        }

        /// <summary>
        /// Updates the device network configuration with the specified settings.
        /// </summary>
        /// <param name="configuration">The new network configuration to apply.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an unsupported WiFi mode or security type is specified.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            // Stop streaming if active
            if (IsStreaming)
            {
                StopStreaming();
            }

            // Set WiFi mode
            switch (configuration.Mode)
            {
                case WifiMode.ExistingNetwork:
                    Send(ScpiMessageProducer.SetNetworkWifiModeExisting);
                    break;
                case WifiMode.SelfHosted:
                    Send(ScpiMessageProducer.SetNetworkWifiModeSelfHosted);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.Mode, "Unsupported WiFi mode.");
            }

            // Set SSID
            Send(ScpiMessageProducer.SetNetworkWifiSsid(configuration.Ssid));

            // Set security type and password
            switch (configuration.SecurityType)
            {
                case WifiSecurityType.None:
                    Send(ScpiMessageProducer.SetNetworkWifiSecurityOpen);
                    break;
                case WifiSecurityType.WpaPskPhrase:
                    Send(ScpiMessageProducer.SetNetworkWifiSecurityWpa);
                    Send(ScpiMessageProducer.SetNetworkWifiPassword(configuration.Password));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.SecurityType, "Unsupported WiFi security type.");
            }

            // Apply configuration
            Send(ScpiMessageProducer.ApplyNetworkLan);

            // Wait for WiFi module to restart
            await Task.Delay(WIFI_MODULE_RESTART_DELAY_MS, cancellationToken);

            // Re-enable LAN interface (SD card and LAN share SPI bus)
            PrepareLanInterface();

            // Save configuration to persist across restarts
            Send(ScpiMessageProducer.SaveNetworkLan);

            // Update local configuration
            _networkConfiguration.Mode = configuration.Mode;
            _networkConfiguration.SecurityType = configuration.SecurityType;
            _networkConfiguration.Ssid = configuration.Ssid;
            _networkConfiguration.Password = configuration.Password;
        }

        /// <summary>
        /// Prepares the SD card interface for use by disabling the LAN interface.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void PrepareSdInterface()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.DisableNetworkLan);
            Send(ScpiMessageProducer.EnableStorageSd);
        }

        /// <summary>
        /// Prepares the LAN interface for use by disabling the SD card interface.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void PrepareLanInterface()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.DisableStorageSd);
            Send(ScpiMessageProducer.EnableNetworkLan);
        }

        /// <summary>
        /// Retrieves the list of files stored on the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the list of files.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines;
            try
            {
                lines = await ExecuteTextCommandAsync(() =>
                {
                    PrepareSdInterface();

                    // Allow the device firmware to complete the SPI bus switch
                    // before querying the SD card. Without this delay, the device
                    // can return SCPI error -200 (Execution error).
                    Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);

                    Send(ScpiMessageProducer.GetSdFileList);
                }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                // If the response contains a SCPI error (transient timing issue),
                // retry once after an additional settle delay.
                if (ContainsScpiError(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken);

                        lines = await ExecuteTextCommandAsync(() =>
                        {
                            PrepareSdInterface();
                            Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);
                            Send(ScpiMessageProducer.GetSdFileList);
                        }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                        if (!ContainsScpiError(lines))
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Restore LAN interface regardless of outcome
                if (IsConnected)
                {
                    PrepareLanInterface();
                }
            }

            var files = SdCardFileListParser.ParseFileList(lines);
            _sdCardFiles = files;
            return files;
        }

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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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

            // SdCardLogFormat integer values map 1:1 to SYSTem:STReam:FORmat SCPI arguments
            var formatCommand = new ScpiMessage($"SYSTem:STReam:FORmat {(int)format}");

            Send(ScpiMessageProducer.EnableStorageSd);
            await Task.Delay(100, cancellationToken);

            Send(ScpiMessageProducer.SetSdLoggingFileName(logFileName));
            await Task.Delay(100, cancellationToken);

            Send(formatCommand);
            await Task.Delay(100, cancellationToken);

            if (!string.IsNullOrWhiteSpace(channelMask))
            {
                Send(ScpiMessageProducer.EnableAdcChannels(channelMask));
                await Task.Delay(100, cancellationToken);
            }

            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));

            _isLoggingToSdCard = true;
            IsStreaming = true;
        }

        /// <summary>
        /// Stops logging data to the SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            StopStreaming();
            Send(ScpiMessageProducer.DisableStorageSd);

            _isLoggingToSdCard = false;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes a file from the SD card.
        /// </summary>
        /// <param name="fileName">The name of the file to delete.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot delete files while logging to SD card.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines;
            try
            {
                lines = await ExecuteTextCommandAsync(() =>
                {
                    PrepareSdInterface();
                    Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);
                    Send(ScpiMessageProducer.DeleteSdFile(fileName));
                    Send(ScpiMessageProducer.GetSdFileList);
                }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                if (ContainsScpiError(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken);

                        lines = await ExecuteTextCommandAsync(() =>
                        {
                            PrepareSdInterface();
                            Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);
                            Send(ScpiMessageProducer.DeleteSdFile(fileName));
                            Send(ScpiMessageProducer.GetSdFileList);
                        }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                        if (!ContainsScpiError(lines))
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (IsConnected)
                {
                    PrepareLanInterface();
                }
            }

            _sdCardFiles = SdCardFileListParser.ParseFileList(lines);
        }

        /// <summary>
        /// Formats the entire SD card, erasing all data.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task FormatSdCardAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot format SD card while logging.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            Send(ScpiMessageProducer.EnableStorageSd);
            Send(ScpiMessageProducer.FormatSdCard);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Downloads a file from the device's SD card over USB.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="destinationStream">The stream to write file contents to.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        public async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            Stream destinationStream,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (!IsUsbConnection)
            {
                throw new InvalidOperationException(
                    "SD card file download is only supported over USB (serial) connections. " +
                    "The SD card and WiFi/LAN share the SPI bus, so file downloads require a USB connection.");
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);
            ArgumentNullException.ThrowIfNull(destinationStream);

            cancellationToken.ThrowIfCancellationRequested();

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot download files while logging to SD card.");
            }

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            var stopwatch = Stopwatch.StartNew();
            long fileSize = 0;

            try
            {
                await ExecuteRawCaptureAsync(async (stream, ct) =>
                {
                    // Prepare SD card interface
                    PrepareSdInterface();

                    // Small delay to let the interface switch settle
                    await Task.Delay(50, ct).ConfigureAwait(false);

                    // Send the SCPI command to request the file
                    Send(ScpiMessageProducer.GetSdFile(fileName));

                    // Receive the file data
                    var receiver = new SdCardFileReceiver(stream);
                    var bytesReceived = await receiver.ReceiveAsync(
                        destinationStream,
                        fileName,
                        progress,
                        timeout: TimeSpan.FromMinutes(30),
                        cancellationToken: ct).ConfigureAwait(false);

                    fileSize = bytesReceived;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Restore LAN interface
                if (IsConnected)
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
        /// Downloads a file from the device's SD card over USB to a temporary file.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file, including the local file path.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        public async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            var tempPath = Path.Combine(Path.GetTempPath(), $"daqifi_{Guid.NewGuid():N}{ext}");
            try
            {
                await using var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    useAsync: true);

                var result = await DownloadSdCardFileAsync(fileName, fileStream, progress, cancellationToken)
                    .ConfigureAwait(false);

                return result with { FilePath = tempPath };
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
                throw;
            }
        }

        /// <summary>
        /// Checks whether any line in the response contains a SCPI error indicator.
        /// These errors (e.g., "**ERROR: -200") can occur transiently when the device
        /// firmware has not finished switching the SPI bus interface.
        /// </summary>
        /// <param name="lines">The response lines to check.</param>
        /// <returns>True if any line contains a SCPI error, false otherwise.</returns>
        private static bool ContainsScpiError(IReadOnlyList<string> lines)
        {
            return lines.Any(line => line.TrimStart().StartsWith("**ERROR", StringComparison.OrdinalIgnoreCase));
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
