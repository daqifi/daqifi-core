using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// Gets the collection of channels populated from device status messages.
        /// </summary>
        /// <remarks>
        /// This collection is populated when <see cref="PopulateChannelsFromStatus"/> is called
        /// with a valid protobuf status message from the device.
        /// </remarks>
        public IReadOnlyList<IChannel> Channels => _channels.AsReadOnly();

        /// <summary>
        /// Gets or sets the current operational state of the device.
        /// </summary>
        public DeviceState State { get; private set; } = DeviceState.Disconnected;

        private ConnectionStatus _status;
        private IMessageProducer<string>? _messageProducer;
        private IMessageConsumer<DaqifiOutMessage>? _messageConsumer;
        private readonly IStreamTransport? _transport;

        /// <summary>
        /// Gets the transport used for device communication, if available.
        /// </summary>
        protected IStreamTransport? Transport => _transport;

        private IProtocolHandler? _protocolHandler;
        private bool _disposed;
        private bool _isInitialized;
        private readonly List<IChannel> _channels = new();
        
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
        /// Occurs when channels have been populated from a device status message.
        /// </summary>
        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated;

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
        /// Temporarily pauses the protobuf message consumer to allow raw byte access to the
        /// underlying transport stream. The consumer is restored when the returned action completes
        /// or is disposed.
        /// </summary>
        /// <param name="rawAction">
        /// An async function that receives the transport stream and performs raw I/O.
        /// The protobuf consumer will not read from the stream while this action is executing.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        protected virtual async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (_transport == null)
            {
                throw new InvalidOperationException("ExecuteRawCaptureAsync requires a transport-based connection.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Stop the protobuf consumer so it doesn't compete for stream bytes
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                    var stopped = _messageConsumer.StopSafely(timeoutMs: 1000);
                    if (!stopped)
                    {
                        _messageConsumer.Stop();
                    }
                }

                // Hand the stream to the caller for raw I/O
                await rawAction(_transport.Stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Restart the protobuf consumer
                if (_messageConsumer != null)
                {
                    _messageConsumer.Start();
                    _messageConsumer.MessageReceived += OnInboundMessageReceived;
                }
            }
        }

        /// <summary>
        /// Executes a text-based command by temporarily switching from the protobuf consumer to a
        /// line-based text consumer, collecting text responses, then restoring the protobuf consumer.
        /// </summary>
        /// <param name="setupAction">An action that sends SCPI commands to the device while the text consumer is active.</param>
        /// <param name="responseTimeoutMs">The time in milliseconds to wait for the first text response after sending commands.</param>
        /// <param name="completionTimeoutMs">The time in milliseconds of inactivity after the first response before considering the response complete. Defaults to 250ms.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A list of text lines received from the device.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        protected virtual async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (_transport == null)
            {
                throw new InvalidOperationException("ExecuteTextCommandAsync requires a transport-based connection.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var collectedLines = new List<string>();
            var stream = _transport.Stream;
            int? originalReadTimeout = null;

            try
            {
                if (stream.CanTimeout)
                {
                    try
                    {
                        originalReadTimeout = stream.ReadTimeout;
                        stream.ReadTimeout = Math.Min(500, Math.Max(100, responseTimeoutMs / 4));
                    }
                    catch
                    {
                        // Some streams may not allow setting read timeout; ignore.
                        originalReadTimeout = null;
                    }
                }

                // Stop the protobuf consumer so it doesn't compete for stream bytes.
                // The serial transport sets ReadTimeout=500ms after connect, so the
                // consumer thread's blocking Read will unblock within 500ms.
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                    var stopped = _messageConsumer.StopSafely(timeoutMs: 1000);
                    if (!stopped)
                    {
                        _messageConsumer.Stop();
                    }
                }

                Trace.WriteLine($"[ExecuteTextCommandAsync] Protobuf consumer stopped at {sw.ElapsedMilliseconds}ms");

                // Create a temporary text consumer on the same stream
                using var textConsumer = new StreamMessageConsumer<string>(
                    _transport.Stream,
                    new LineBasedMessageParser());

                textConsumer.MessageReceived += (_, e) =>
                {
                    collectedLines.Add(e.Message.Data);
                };

                textConsumer.Start();
                await Task.Delay(50, cancellationToken);

                Trace.WriteLine($"[ExecuteTextCommandAsync] Text consumer started at {sw.ElapsedMilliseconds}ms");

                // Execute the setup action (sends SCPI commands)
                setupAction();

                Trace.WriteLine($"[ExecuteTextCommandAsync] Setup action completed at {sw.ElapsedMilliseconds}ms");

                // Wait for responses using a two-phase inactivity-based timeout:
                // Phase 1: Wait up to responseTimeoutMs for the first response.
                // Phase 2: After receiving data, wait completionTimeoutMs of inactivity to finish.
                var lastMessageTime = DateTime.UtcNow;
                var maxWait = TimeSpan.FromMilliseconds(responseTimeoutMs * 5);
                var startTime = DateTime.UtcNow;
                var hasReceivedAny = false;

                while (DateTime.UtcNow - startTime < maxWait)
                {
                    var previousCount = collectedLines.Count;
                    await Task.Delay(50, cancellationToken);
                    if (collectedLines.Count > previousCount)
                    {
                        lastMessageTime = DateTime.UtcNow;
                        if (!hasReceivedAny)
                        {
                            hasReceivedAny = true;
                            Trace.WriteLine($"[ExecuteTextCommandAsync] First response at {sw.ElapsedMilliseconds}ms");
                        }
                    }

                    var elapsed = DateTime.UtcNow - lastMessageTime;

                    if (hasReceivedAny)
                    {
                        // Phase 2: short completion timeout after first data
                        if (elapsed >= TimeSpan.FromMilliseconds(completionTimeoutMs))
                        {
                            break;
                        }
                    }
                    else
                    {
                        // Phase 1: full initial timeout waiting for first data
                        if (elapsed >= TimeSpan.FromMilliseconds(responseTimeoutMs))
                        {
                            break;
                        }
                    }
                }

                Trace.WriteLine($"[ExecuteTextCommandAsync] Collection complete at {sw.ElapsedMilliseconds}ms, {collectedLines.Count} lines");

                // Stop the text consumer
                textConsumer.StopSafely();
            }
            finally
            {
                if (originalReadTimeout.HasValue && stream.CanTimeout)
                {
                    try
                    {
                        stream.ReadTimeout = originalReadTimeout.Value;
                    }
                    catch
                    {
                        // Ignore failures when restoring timeout.
                    }
                }

                // Restart the protobuf consumer
                if (_messageConsumer != null)
                {
                    _messageConsumer.Start();
                    _messageConsumer.MessageReceived += OnInboundMessageReceived;
                }

                Trace.WriteLine($"[ExecuteTextCommandAsync] Total elapsed: {sw.ElapsedMilliseconds}ms");
            }

            return collectedLines;
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

            // Populate channels from the status message
            PopulateChannelsFromStatus(message);

            // Raise event for external consumers
            var inboundMessage = new ProtobufMessage(message);
            OnMessageReceived(inboundMessage);
        }

        /// <summary>
        /// Populates the device channels from a protobuf status message.
        /// </summary>
        /// <param name="message">The protobuf status message containing channel configuration.</param>
        /// <remarks>
        /// This method creates channel instances based on the channel counts and calibration
        /// parameters in the status message. Existing channels are cleared before repopulating
        /// to handle device reconnection scenarios.
        ///
        /// For analog channels, calibration parameters (CalM, CalB, InternalScaleM, PortRange)
        /// are extracted from the message. If there's a mismatch between the declared channel
        /// count and the available calibration data, default values are used for missing parameters.
        ///
        /// For digital channels, only the channel count is used to create instances.
        /// </remarks>
        public virtual void PopulateChannelsFromStatus(DaqifiOutMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            // Clear existing channels before repopulating
            _channels.Clear();

            var analogCount = 0;
            var digitalCount = 0;

            // Populate analog input channels
            if (message.AnalogInPortNum > 0)
            {
                analogCount = PopulateAnalogChannels(message);
            }

            // Populate digital channels
            if (message.DigitalPortNum > 0)
            {
                digitalCount = PopulateDigitalChannels(message);
            }

            // Raise the ChannelsPopulated event with a snapshot to prevent mutations affecting handlers
            var channelsSnapshot = _channels.ToArray();
            ChannelsPopulated?.Invoke(this, new ChannelsPopulatedEventArgs(
                Array.AsReadOnly(channelsSnapshot),
                analogCount,
                digitalCount));
        }

        /// <summary>
        /// Populates analog channels from the protobuf message.
        /// </summary>
        /// <param name="message">The protobuf message containing analog channel data.</param>
        /// <returns>The number of analog channels created.</returns>
        private int PopulateAnalogChannels(DaqifiOutMessage message)
        {
            var analogInPortRanges = message.AnalogInPortRange;
            var analogInCalibrationBValues = message.AnalogInCalB;
            var analogInCalibrationMValues = message.AnalogInCalM;
            var analogInInternalScaleMValues = message.AnalogInIntScaleM;
            var analogInResolution = message.AnalogInRes;

            var count = (int)message.AnalogInPortNum;

            for (var i = 0; i < count; i++)
            {
                var channel = new AnalogChannel(i, analogInResolution > 0 ? analogInResolution : 65535)
                {
                    Name = $"AI{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = false,
                    CalibrationB = GetWithDefault(analogInCalibrationBValues, i, 0.0f),
                    CalibrationM = GetWithDefault(analogInCalibrationMValues, i, 1.0f),
                    InternalScaleM = GetWithDefault(analogInInternalScaleMValues, i, 1.0f),
                    PortRange = GetWithDefault(analogInPortRanges, i, 1.0f)
                };

                _channels.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Populates digital channels from the protobuf message.
        /// </summary>
        /// <param name="message">The protobuf message containing digital channel data.</param>
        /// <returns>The number of digital channels created.</returns>
        private int PopulateDigitalChannels(DaqifiOutMessage message)
        {
            var count = (int)message.DigitalPortNum;

            for (var i = 0; i < count; i++)
            {
                var channel = new DigitalChannel(i)
                {
                    Name = $"DIO{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = true
                };

                _channels.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Gets a value from a list with a default fallback if the index is out of range.
        /// </summary>
        /// <param name="list">The list to get the value from.</param>
        /// <param name="index">The index to retrieve.</param>
        /// <param name="defaultValue">The default value if the index is out of range.</param>
        /// <returns>The value at the index or the default value.</returns>
        private static T GetWithDefault<T>(IList<T> list, int index, T defaultValue)
        {
            if (list.Count > index)
            {
                return list[index];
            }
            return defaultValue;
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
    }
} 
