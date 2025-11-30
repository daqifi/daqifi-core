using System;

#nullable enable

namespace Daqifi.Core.Device.Configuration
{
    /// <summary>
    /// Represents the network configuration for a DAQiFi device.
    /// </summary>
    public class NetworkConfiguration
    {
        /// <summary>
        /// Gets or sets the WiFi mode (Existing Network or Self-Hosted).
        /// </summary>
        public WifiMode Mode { get; set; }

        /// <summary>
        /// Gets or sets the WiFi security type (None or WPA2-PSK).
        /// </summary>
        public WifiSecurityType SecurityType { get; set; }

        /// <summary>
        /// Gets or sets the SSID (network name).
        /// </summary>
        public string? Ssid { get; set; }

        /// <summary>
        /// Gets or sets the network password. Required when SecurityType is WPA2-PSK.
        /// </summary>
        public string? Password { get; set; }

        // Read-only properties (from device)

        /// <summary>
        /// Gets or sets the IP address assigned to the device (read-only, retrieved from device).
        /// </summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// Gets or sets the MAC address of the device (read-only, retrieved from device).
        /// </summary>
        public string? MacAddress { get; set; }

        /// <summary>
        /// Gets or sets the gateway address (read-only, retrieved from device).
        /// </summary>
        public string? Gateway { get; set; }

        /// <summary>
        /// Gets or sets the subnet mask (read-only, retrieved from device).
        /// </summary>
        public string? SubnetMask { get; set; }
    }

    /// <summary>
    /// Specifies the WiFi operation mode.
    /// </summary>
    public enum WifiMode
    {
        /// <summary>
        /// Connect to an existing WiFi network.
        /// </summary>
        ExistingNetwork = 1,

        /// <summary>
        /// Create a self-hosted access point.
        /// </summary>
        SelfHosted = 4
    }

    /// <summary>
    /// Specifies the WiFi security type.
    /// </summary>
    public enum WifiSecurityType
    {
        /// <summary>
        /// No security (open network).
        /// </summary>
        None = 0,

        /// <summary>
        /// WPA2-PSK (Pre-Shared Key) security with passphrase.
        /// </summary>
        WpaPskPhrase = 3
    }
}
