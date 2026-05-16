using System.Net;

namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// Represents the network configuration settings for a DAQiFi device.
    /// </summary>
    public class NetworkConfiguration
    {
        /// <summary>
        /// Gets or sets the WiFi operating mode.
        /// </summary>
        public WifiMode Mode { get; set; } = WifiMode.SelfHosted;

        /// <summary>
        /// Gets or sets the WiFi security type.
        /// </summary>
        public WifiSecurityType SecurityType { get; set; } = WifiSecurityType.WpaPskPhrase;

        /// <summary>
        /// Gets or sets the network SSID (Service Set Identifier).
        /// </summary>
        public string Ssid { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the network password.
        /// </summary>
        /// <remarks>
        /// This property is only used when <see cref="SecurityType"/> is set to a value
        /// other than <see cref="WifiSecurityType.None"/>.
        /// </remarks>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the static IP address to assign to the device.
        /// </summary>
        /// <remarks>
        /// A null value means "leave unchanged" — the device retains its current
        /// IP configuration (typically DHCP). Provide all three of
        /// <see cref="StaticIP"/>, <see cref="SubnetMask"/>, and <see cref="Gateway"/>
        /// to fully define a static IP configuration.
        /// </remarks>
        public IPAddress? StaticIP { get; set; }

        /// <summary>
        /// Gets or sets the subnet mask to use with <see cref="StaticIP"/>.
        /// </summary>
        /// <remarks>
        /// A null value means "leave unchanged" on the device.
        /// </remarks>
        public IPAddress? SubnetMask { get; set; }

        /// <summary>
        /// Gets or sets the default gateway to use with <see cref="StaticIP"/>.
        /// </summary>
        /// <remarks>
        /// A null value means "leave unchanged" on the device.
        /// </remarks>
        public IPAddress? Gateway { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkConfiguration"/> class
        /// with default values.
        /// </summary>
        public NetworkConfiguration()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkConfiguration"/> class
        /// with the specified WiFi settings.
        /// </summary>
        /// <param name="mode">The WiFi operating mode.</param>
        /// <param name="securityType">The WiFi security type.</param>
        /// <param name="ssid">The network SSID.</param>
        /// <param name="password">The network password.</param>
        public NetworkConfiguration(WifiMode mode, WifiSecurityType securityType, string ssid, string password)
        {
            Mode = mode;
            SecurityType = securityType;
            Ssid = ssid;
            Password = password;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkConfiguration"/> class
        /// with the specified WiFi and static IP settings.
        /// </summary>
        /// <param name="mode">The WiFi operating mode.</param>
        /// <param name="securityType">The WiFi security type.</param>
        /// <param name="ssid">The network SSID.</param>
        /// <param name="password">The network password.</param>
        /// <param name="staticIP">The static IP address, or null to leave unchanged.</param>
        /// <param name="subnetMask">The subnet mask, or null to leave unchanged.</param>
        /// <param name="gateway">The default gateway, or null to leave unchanged.</param>
        public NetworkConfiguration(
            WifiMode mode,
            WifiSecurityType securityType,
            string ssid,
            string password,
            IPAddress? staticIP,
            IPAddress? subnetMask,
            IPAddress? gateway)
        {
            Mode = mode;
            SecurityType = securityType;
            Ssid = ssid;
            Password = password;
            StaticIP = staticIP;
            SubnetMask = subnetMask;
            Gateway = gateway;
        }

        /// <summary>
        /// Creates a copy of this network configuration.
        /// </summary>
        /// <returns>A new <see cref="NetworkConfiguration"/> instance with the same values.</returns>
        public NetworkConfiguration Clone()
        {
            return new NetworkConfiguration(
                Mode,
                SecurityType,
                Ssid,
                Password,
                StaticIP,
                SubnetMask,
                Gateway);
        }
    }
}
