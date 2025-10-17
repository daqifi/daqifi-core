namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents the connection status of a device.
    /// </summary>
    public enum ConnectionStatus
    {
        /// <summary>
        /// The device is disconnected.
        /// </summary>
        Disconnected,

        /// <summary>
        /// The device is in the process of connecting.
        /// </summary>
        Connecting,

        /// <summary>
        /// The device is connected.
        /// </summary>
        Connected,

        /// <summary>
        /// The device connection has been lost.
        /// </summary>
        Lost,

        /// <summary>
        /// The device is retrying connection after a failure.
        /// </summary>
        Retrying,

        /// <summary>
        /// The device connection failed after all retry attempts.
        /// </summary>
        Failed
    }
} 