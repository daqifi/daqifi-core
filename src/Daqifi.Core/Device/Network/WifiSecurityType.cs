using System.ComponentModel;

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
        [Description("None (Open Network)")]
        None = 0,

        /// <summary>
        /// WPA/WPA2 Personal using a pre-shared key passphrase.
        /// </summary>
        [Description("WPA Pass Phrase")]
        WpaPskPhrase = 3
    }
}
