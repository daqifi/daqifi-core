namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// Specifies the WiFi security type for network connections.
    /// </summary>
    public enum WifiSecurityType
    {
        /// <summary>
        /// No security (open network).
        /// </summary>
        None = 0,

        /// <summary>
        /// WPA/WPA2 Personal using a pre-shared key passphrase.
        /// </summary>
        WpaPskPhrase = 3
    }
}
