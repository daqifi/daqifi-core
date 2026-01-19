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
        /// Initializes a new instance of the <see cref="NetworkConfiguration"/> class
        /// with default values.
        /// </summary>
        public NetworkConfiguration()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkConfiguration"/> class
        /// with the specified settings.
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
        /// Creates a copy of this network configuration.
        /// </summary>
        /// <returns>A new <see cref="NetworkConfiguration"/> instance with the same values.</returns>
        public NetworkConfiguration Clone()
        {
            return new NetworkConfiguration(Mode, SecurityType, Ssid, Password);
        }
    }
}
