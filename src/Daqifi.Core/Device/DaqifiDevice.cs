using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using System;
using System.Net;

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

        private ConnectionStatus _status;
        private readonly IMessageProducer<string>? _messageProducer;
        private bool _disposed;
        
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
        /// Connects to the device.
        /// </summary>
        public void Connect()
        {
            Status = ConnectionStatus.Connecting;
            
            // Start message producer if available
            _messageProducer?.Start();
            
            Status = ConnectionStatus.Connected;
        }

        /// <summary>
        /// Disconnects from the device.
        /// </summary>
        public void Disconnect()
        {
            // Stop message producer safely if available
            _messageProducer?.StopSafely();
            
            Status = ConnectionStatus.Disconnected;
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
        /// Disposes the device and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _messageProducer?.Dispose();
                _disposed = true;
            }
        }
    }
} 