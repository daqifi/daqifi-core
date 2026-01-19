using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Network;
using System;
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
    public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, INetworkConfigurable
    {
        /// <summary>
        /// The delay in milliseconds to wait for the WiFi module to restart after applying configuration.
        /// </summary>
        private const int WIFI_MODULE_RESTART_DELAY_MS = 2000;

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        public bool IsStreaming { get; private set; }

        /// <summary>
        /// Gets or sets the streaming frequency in Hz (samples per second).
        /// </summary>
        public int StreamingFrequency { get; set; }

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
    }
}