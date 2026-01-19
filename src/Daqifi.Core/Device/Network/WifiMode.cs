namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// Specifies the WiFi operating mode for the device.
    /// </summary>
    public enum WifiMode
    {
        /// <summary>
        /// Device connects to an existing WiFi network as a client.
        /// </summary>
        ExistingNetwork = 1,

        /// <summary>
        /// Device creates its own WiFi network as an access point.
        /// </summary>
        SelfHosted = 4
    }
}
