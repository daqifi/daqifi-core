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
        /// The device is retrying connection after a failure — in particular, it is between
        /// automatic reconnect attempts after a drop (see <see cref="ReconnectOptions"/>). Only
        /// ever seen when reconnect is enabled; with the default policy a drop stops at
        /// <see cref="Lost"/>.
        /// </summary>
        Retrying,

        /// <summary>
        /// The device connection failed after all retry attempts — automatic reconnection ran out
        /// of attempts and gave up (see <see cref="ReconnectOptions.MaxAttempts"/>). Terminal:
        /// nothing further will be attempted without a new <c>Connect()</c>. Only ever seen when
        /// reconnect is enabled.
        /// </summary>
        Failed
    }
} 