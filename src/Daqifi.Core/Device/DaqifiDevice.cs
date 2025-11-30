using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Configuration;
using Daqifi.Core.Device.Protocol;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that can be connected to and communicated with.
    /// This is the base implementation of the IDevice interface.
    /// </summary>
    public class DaqifiDevice : IDevice, INetworkConfigurable, IDisposable
    {
        /// <summary>
        /// Gets the name of the device.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the IP address of the device, if known.
        /// </summary>
        public IPAddress? IpAddress { get; }

        /// <summary>
        /// Gets a value indicating whether the device is currently connected.
        /// </summary>
        public bool IsConnected => Status == ConnectionStatus.Connected;

        /// <summary>
        /// Gets the device metadata containing part number, firmware version, etc.
        /// </summary>
        public DeviceMetadata Metadata { get; } = new DeviceMetadata();

        /// <summary>
        /// Gets or sets the current operational state of the device.
        /// </summary>
        public DeviceState State { get; private set; } = DeviceState.Disconnected;

        /// <summary>
        /// Gets the current network configuration of the device.
        /// </summary>
        public NetworkConfiguration NetworkConfiguration { get; } = new NetworkConfiguration();

        private ConnectionStatus _status;
        private IMessageProducer<string>? _messageProducer;
        private IMessageConsumer<DaqifiOutMessage>? _messageConsumer;
        private readonly IStreamTransport? _transport;
        private IProtocolHandler? _protocolHandler;
        private bool _disposed;
        private bool _isInitialized;
        
        /// <summary>
        /// Gets the current connection status of the device.
        /// </summary>
        public ConnectionStatus Status
        {
            get => _status;
            private set
            {
                if (_status == value) return;
                _status = value;
                StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
            }
        }

        /// <summary>
        /// Occurs when the device status changes.
        /// </summary>
        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
        
        /// <summary>
        /// Occurs when a message is received from the device.
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        public DaqifiDevice(string name, IPAddress? ipAddress = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a message producer.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="stream">The stream for device communication.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        public DaqifiDevice(string name, Stream stream, IPAddress? ipAddress = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
            _messageProducer = new MessageProducer<string>(stream);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a transport.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="transport">The transport for device communication.</param>
        public DaqifiDevice(string name, IStreamTransport transport)
        {
            Name = name;
            _status = ConnectionStatus.Disconnected;
            _transport = transport;
            
            // Subscribe to transport status changes
            _transport.StatusChanged += OnTransportStatusChanged;
        }

        /// <summary>
        /// Connects to the device.
        /// </summary>
        public void Connect()
        {
            Status = ConnectionStatus.Connecting;
            State = DeviceState.Connecting;

            try
            {
                // Connect transport if available
                _transport?.Connect();

                // Create message producer and consumer from transport if needed
                if (_transport != null)
                {
                    if (_messageProducer == null)
                    {
                        _messageProducer = new MessageProducer<string>(_transport.Stream);
                    }

                    if (_messageConsumer == null)
                    {
                        _messageConsumer = new StreamMessageConsumer<DaqifiOutMessage>(
                            _transport.Stream,
                            new ProtobufMessageParser());
                    }
                }

                // Start message producer and consumer if available
                _messageProducer?.Start();
                _messageConsumer?.Start();

                Status = ConnectionStatus.Connected;
                State = DeviceState.Connected;
            }
            catch
            {
                Status = ConnectionStatus.Disconnected;
                State = DeviceState.Disconnected;
                throw;
            }
        }

        /// <summary>
        /// Disconnects from the device.
        /// </summary>
        public void Disconnect()
        {
            try
            {
                // Unsubscribe from message consumer events
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                }

                // Stop message consumer and producer safely if available
                _messageConsumer?.StopSafely();
                _messageProducer?.StopSafely();

                // Disconnect transport if available
                _transport?.Disconnect();
            }
            finally
            {
                Status = ConnectionStatus.Disconnected;
                State = DeviceState.Disconnected;
                _isInitialized = false;
            }
        }

        /// <summary>
        /// Sends a message to the device.
        /// </summary>
        /// <typeparam name="T">The type of the message data payload.</typeparam>
        /// <param name="message">The message to send to the device.</param>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public virtual void Send<T>(IOutboundMessage<T> message)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            // Use message producer if available and message is string-based
            if (_messageProducer != null && message is IOutboundMessage<string> stringMessage)
            {
                _messageProducer.Send(stringMessage);
            }
            else
            {
                // Fallback for backward compatibility - no implementation yet
                // This will be enhanced in later steps when we add transport abstraction
                throw new NotImplementedException("Direct message sending without message producer is not yet implemented. Use constructor with Stream parameter.");
            }
        }

        /// <summary>
        /// Raises the <see cref="MessageReceived"/> event when a message is received from the device.
        /// </summary>
        /// <param name="message">The message received from the device.</param>
        protected virtual void OnMessageReceived(IInboundMessage<object> message)
        {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
        }

        /// <summary>
        /// Handles transport status changes and updates device connection status accordingly.
        /// </summary>
        /// <param name="sender">The transport that raised the event.</param>
        /// <param name="e">The transport status event arguments.</param>
        private void OnTransportStatusChanged(object? sender, TransportStatusEventArgs e)
        {
            if (e.IsConnected)
            {
                // Transport connected, but device status is managed by Connect() method
            }
            else
            {
                // Transport disconnected, update device status
                if (Status == ConnectionStatus.Connected)
                {
                    Status = ConnectionStatus.Lost;
                }
            }
        }

        /// <summary>
        /// Disposes the device and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _messageConsumer?.Dispose();
                _messageProducer?.Dispose();
                _transport?.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// Initializes the device by running the standard initialization sequence.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// The initialization sequence includes:
        /// 1. Disable device echo
        /// 2. Stop any running streaming
        /// 3. Turn device on (if needed)
        /// 4. Set protobuf message format
        /// 5. Query device info and capabilities
        ///
        /// Delays are added between commands to give the device time to process each request.
        /// </remarks>
        public virtual async Task InitializeAsync()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected before initialization.");
            }

            if (_isInitialized)
            {
                return; // Already initialized
            }

            State = DeviceState.Initializing;

            try
            {
                // Set up protocol handler for status messages
                _protocolHandler = new ProtobufProtocolHandler(
                    statusMessageHandler: OnStatusMessageReceived,
                    streamMessageHandler: OnStreamMessageReceived
                );

                // Wire up message consumer to route messages through protocol handler
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived += OnInboundMessageReceived;
                }

                // Standard initialization sequence with delays between commands
                Send(ScpiMessageProducer.DisableDeviceEcho);
                await Task.Delay(100);

                Send(ScpiMessageProducer.StopStreaming);
                await Task.Delay(100);

                Send(ScpiMessageProducer.TurnDeviceOn);
                await Task.Delay(100);

                Send(ScpiMessageProducer.SetProtobufStreamFormat);
                await Task.Delay(100);

                Send(ScpiMessageProducer.GetDeviceInfo);
                await Task.Delay(500); // Longer delay to allow device info response

                _isInitialized = true;
                State = DeviceState.Ready;
            }
            catch (Exception)
            {
                State = DeviceState.Error;
                throw;
            }
        }

        /// <summary>
        /// Handles status messages received from the device during initialization.
        /// </summary>
        /// <param name="message">The status message from the device.</param>
        protected virtual void OnStatusMessageReceived(DaqifiOutMessage message)
        {
            // Update device metadata
            Metadata.UpdateFromProtobuf(message);

            // Raise event for external consumers
            var inboundMessage = new ProtobufMessage(message);
            OnMessageReceived(inboundMessage);
        }

        /// <summary>
        /// Handles streaming data messages received from the device.
        /// </summary>
        /// <param name="message">The streaming message from the device.</param>
        protected virtual void OnStreamMessageReceived(DaqifiOutMessage message)
        {
            // Raise event for external consumers
            var inboundMessage = new ProtobufMessage(message);
            OnMessageReceived(inboundMessage);
        }

        /// <summary>
        /// Handles inbound messages from the message consumer and routes them through the protocol handler.
        /// </summary>
        /// <param name="sender">The message consumer that raised the event.</param>
        /// <param name="e">The message received event arguments.</param>
        private void OnInboundMessageReceived(object? sender, MessageReceivedEventArgs<DaqifiOutMessage> e)
        {
            // Convert to generic inbound message and route through protocol handler
            var genericMessage = new GenericInboundMessage<object>(e.Message.Data);

            // Route through protocol handler if available
            if (_protocolHandler != null && _protocolHandler.CanHandle(genericMessage))
            {
                // Fire and forget - we don't need to wait for the handler to complete
                _ = _protocolHandler.HandleAsync(genericMessage);
            }
        }

        #region Network Configuration Implementation

        /// <summary>
        /// Retrieves the current network configuration from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The current network configuration.</returns>
        public virtual async Task<NetworkConfiguration> GetNetworkConfigAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to get network configuration.");
            }

            // Request device info which contains network configuration
            Send(ScpiMessageProducer.GetDeviceInfo);

            // Wait for device to respond and update metadata
            await Task.Delay(500, cancellationToken);

            // Update network configuration from metadata
            UpdateNetworkConfigurationFromMetadata();

            return NetworkConfiguration;
        }

        /// <summary>
        /// Sets the network configuration on the device without applying it.
        /// Configuration must be applied using <see cref="ApplyNetworkConfigAsync"/> to take effect.
        /// </summary>
        /// <param name="config">The network configuration to set.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        public virtual async Task SetNetworkConfigAsync(NetworkConfiguration config, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to set network configuration.");
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            // Validate configuration
            ValidateNetworkConfiguration(config);

            // Send SCPI command sequence
            // 1. Set WiFi mode (Existing Network or Self-Hosted)
            if (config.Mode == WifiMode.ExistingNetwork)
            {
                Send(ScpiMessageProducer.SetNetworkWifiModeExisting);
            }
            else if (config.Mode == WifiMode.SelfHosted)
            {
                Send(ScpiMessageProducer.SetNetworkWifiModeSelfHosted);
            }
            await Task.Delay(100, cancellationToken);

            // 2. Set SSID
            if (!string.IsNullOrEmpty(config.Ssid))
            {
                Send(ScpiMessageProducer.SetNetworkWifiSsid(config.Ssid));
                await Task.Delay(100, cancellationToken);
            }

            // 3. Set security type
            if (config.SecurityType == WifiSecurityType.None)
            {
                Send(ScpiMessageProducer.SetNetworkWifiSecurityOpen);
            }
            else if (config.SecurityType == WifiSecurityType.WpaPskPhrase)
            {
                Send(ScpiMessageProducer.SetNetworkWifiSecurityWpa);
            }
            await Task.Delay(100, cancellationToken);

            // 4. Set password (if security is WPA)
            if (config.SecurityType == WifiSecurityType.WpaPskPhrase && !string.IsNullOrEmpty(config.Password))
            {
                Send(ScpiMessageProducer.SetNetworkWifiPassword(config.Password));
                await Task.Delay(100, cancellationToken);
            }

            // Update local configuration
            NetworkConfiguration.Mode = config.Mode;
            NetworkConfiguration.Ssid = config.Ssid;
            NetworkConfiguration.SecurityType = config.SecurityType;
            NetworkConfiguration.Password = config.Password;
        }

        /// <summary>
        /// Applies the network configuration to the device.
        /// This will restart the WiFi module, which may take up to 2 seconds.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        public virtual async Task ApplyNetworkConfigAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to apply network configuration.");
            }

            // Apply the configuration
            Send(ScpiMessageProducer.ApplyNetworkLan);

            // Wait for WiFi module to restart (2 seconds as per plan)
            await Task.Delay(2000, cancellationToken);

            // Re-enable LAN if needed (SPI bus conflict with SD card)
            Send(ScpiMessageProducer.EnableNetworkLan);
            await Task.Delay(100, cancellationToken);
        }

        /// <summary>
        /// Saves the network configuration to non-volatile memory on the device.
        /// Configuration will persist across device reboots.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        public virtual async Task SaveNetworkConfigAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to save network configuration.");
            }

            // Save configuration to NVM
            Send(ScpiMessageProducer.SaveNetworkLan);
            await Task.Delay(100, cancellationToken);
        }

        /// <summary>
        /// Validates the network configuration before applying it.
        /// </summary>
        /// <param name="config">The configuration to validate.</param>
        /// <exception cref="ArgumentException">Thrown when configuration is invalid.</exception>
        private void ValidateNetworkConfiguration(NetworkConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.Ssid))
            {
                throw new ArgumentException("SSID cannot be empty.", nameof(config));
            }

            if (config.SecurityType == WifiSecurityType.WpaPskPhrase && string.IsNullOrWhiteSpace(config.Password))
            {
                throw new ArgumentException("Password is required when using WPA security.", nameof(config));
            }
        }

        /// <summary>
        /// Updates the network configuration from device metadata.
        /// </summary>
        private void UpdateNetworkConfigurationFromMetadata()
        {
            // Update read-only fields from device metadata
            NetworkConfiguration.IpAddress = Metadata.IpAddress;
            NetworkConfiguration.MacAddress = Metadata.MacAddress;
            NetworkConfiguration.Ssid = Metadata.Ssid;
            // Gateway and SubnetMask would be updated here if available in metadata
        }

        /// <summary>
        /// Gets the current WiFi signal strength from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The WiFi signal strength (RSSI) in dBm, or null if not available.</returns>
        public virtual async Task<int?> GetWifiSignalStrengthAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to get WiFi signal strength.");
            }

            // Request device info which contains signal strength
            Send(ScpiMessageProducer.GetDeviceInfo);

            // Wait for device to respond and update metadata
            await Task.Delay(500, cancellationToken);

            return Metadata.SignalStrength;
        }

        /// <summary>
        /// Gets the comprehensive network status from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The network status including IP, MAC, signal strength, etc.</returns>
        public virtual async Task<NetworkStatus> GetNetworkStatusAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected to get network status.");
            }

            // Request device info which contains network status
            Send(ScpiMessageProducer.GetDeviceInfo);

            // Wait for device to respond and update metadata
            await Task.Delay(500, cancellationToken);

            // Build comprehensive status from metadata
            return new NetworkStatus
            {
                IsConnected = IsConnected,
                IpAddress = Metadata.IpAddress,
                MacAddress = Metadata.MacAddress,
                Ssid = Metadata.Ssid,
                SignalStrength = Metadata.SignalStrength,
                Gateway = null, // Not available in current metadata
                SubnetMask = null // Not available in current metadata
            };
        }

        #endregion
    }
} 