using Daqifi.Core.Communication.Messages;
using System;
using System.Net;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Base interface for all DAQiFi devices.
    /// </summary>
    public interface IDevice
    {
        /// <summary>
        /// Gets the name of the device.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the IP address of the device.
        /// </summary>
        IPAddress? IpAddress { get; }

        /// <summary>
        /// Gets a value indicating whether the device is connected.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the current connection status of the device.
        /// </summary>
        ConnectionStatus Status { get; }

        /// <summary>
        /// Occurs when the device status changes.
        /// </summary>
        event EventHandler<DeviceStatusEventArgs> StatusChanged;

        /// <summary>
        /// Occurs when a message is received from the device.
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// Connects to the device.
        /// </summary>
        void Connect();

        /// <summary>
        /// Disconnects from the device.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Sends a message to the device.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send<T>(IOutboundMessage<T> message);
    }
} // test change
