using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;
using System;
using System.Net;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that can be connected to and communicated with.
    /// This is the base implementation of the IDevice interface.
    /// </summary>
    public class DaqifiDevice : IDevice, IDisposable
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

        private ConnectionStatus _status;
        private IMessageProducer<string>? _messageProducer;
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

                // Create message producer from transport if needed
                if (_transport != null && _messageProducer == null)
                {
                    _messageProducer = new MessageProducer<string>(_transport.Stream);
                }

                // Start message producer if available
                _messageProducer?.Start();

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
                // Stop message producer safely if available
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

                // Standard initialization sequence
                await DisableDeviceEchoAsync();
                await StopStreamingAsync();
                await TurnDeviceOnAsync();
                await SetProtobufMessageFormatAsync();
                await QueryDeviceInfoAsync();

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
        /// Disables the device echo functionality.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task DisableDeviceEchoAsync()
        {
            Send(ScpiMessageProducer.DisableDeviceEcho);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops any active data streaming on the device.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task StopStreamingAsync()
        {
            Send(ScpiMessageProducer.StopStreaming);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Turns the device on.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task TurnDeviceOnAsync()
        {
            Send(ScpiMessageProducer.TurnDeviceOn);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets the message format to Protocol Buffer.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task SetProtobufMessageFormatAsync()
        {
            Send(ScpiMessageProducer.SetProtobufStreamFormat);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Queries the device for its information and capabilities.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task QueryDeviceInfoAsync()
        {
            Send(ScpiMessageProducer.GetDeviceInfo);
            return Task.CompletedTask;
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
    }
} 