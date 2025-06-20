using System;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Provides data for the <see cref="IDevice.StatusChanged"/> event.
    /// </summary>
    public class DeviceStatusEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the connection status.
        /// </summary>
        public ConnectionStatus Status { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceStatusEventArgs"/> class.
        /// </summary>
        /// <param name="status">The connection status.</param>
        public DeviceStatusEventArgs(ConnectionStatus status)
        {
            Status = status;
        }
    }
} 