using System;

namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// Thrown by <see cref="INetworkConfigurable.UpdateNetworkConfigurationAsync"/> when it is
    /// invoked over a WiFi/TCP control transport instead of USB.
    /// </summary>
    /// <remarks>
    /// The reconfiguration sequence applies the new LAN settings with
    /// <c>SYSTem:COMMunicate:LAN:APPLY</c> (and later re-enables the interface), which restarts the
    /// WiFi module. Over a WiFi/TCP control connection that restart tears down the very channel
    /// carrying the command stream <b>before</b> the trailing <c>SYSTem:COMMunicate:LAN:SAVE</c> can
    /// be delivered, leaving the device with the new settings applied-but-not-persisted (lost on the
    /// next power cycle) or the tail of the sequence undelivered entirely. Because the outcome is
    /// unrecoverable from the caller's side once the connection drops, the operation is rejected up
    /// front over WiFi rather than dispatched into that race. Reconnect the device over USB and
    /// reissue the reconfiguration. See issue #352.
    /// </remarks>
    public sealed class NetworkReconfigurationRequiresUsbException : InvalidOperationException
    {
        private const string DefaultMessage =
            "Network reconfiguration requires a USB connection. Applying LAN settings restarts the " +
            "WiFi module, which would drop a WiFi/TCP control connection before the configuration is " +
            "saved. Reconnect the device over USB and retry.";

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkReconfigurationRequiresUsbException"/>
        /// class with a default explanatory message.
        /// </summary>
        public NetworkReconfigurationRequiresUsbException()
            : base(DefaultMessage)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkReconfigurationRequiresUsbException"/>
        /// class with a custom message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public NetworkReconfigurationRequiresUsbException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkReconfigurationRequiresUsbException"/>
        /// class with a custom message and inner exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of this exception.</param>
        public NetworkReconfigurationRequiresUsbException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
