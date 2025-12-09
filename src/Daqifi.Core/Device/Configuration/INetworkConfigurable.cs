using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Configuration
{
    /// <summary>
    /// Interface for devices that support network configuration.
    /// </summary>
    public interface INetworkConfigurable
    {
        /// <summary>
        /// Gets the current network configuration of the device.
        /// </summary>
        NetworkConfiguration NetworkConfiguration { get; }

        /// <summary>
        /// Retrieves the current network configuration from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The current network configuration.</returns>
        Task<NetworkConfiguration> GetNetworkConfigAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the network configuration on the device without applying it.
        /// Configuration must be applied using <see cref="ApplyNetworkConfigAsync"/> to take effect.
        /// </summary>
        /// <param name="config">The network configuration to set.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        Task SetNetworkConfigAsync(NetworkConfiguration config, CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies the network configuration to the device.
        /// This will restart the WiFi module, which may take up to 2 seconds.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        Task ApplyNetworkConfigAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the network configuration to non-volatile memory on the device.
        /// Configuration will persist across device reboots.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        Task SaveNetworkConfigAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current WiFi signal strength from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The WiFi signal strength (RSSI) in dBm, or null if not available.</returns>
        Task<int?> GetWifiSignalStrengthAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the comprehensive network status from the device.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The network status including IP, MAC, signal strength, etc.</returns>
        Task<NetworkStatus> GetNetworkStatusAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the comprehensive network status of a device.
    /// </summary>
    public class NetworkStatus
    {
        /// <summary>
        /// Gets or sets a value indicating whether the network is connected.
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// Gets or sets the IP address of the device.
        /// </summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// Gets or sets the MAC address of the device.
        /// </summary>
        public string? MacAddress { get; set; }

        /// <summary>
        /// Gets or sets the SSID of the connected network.
        /// </summary>
        public string? Ssid { get; set; }

        /// <summary>
        /// Gets or sets the WiFi signal strength (RSSI) in dBm.
        /// </summary>
        public int? SignalStrength { get; set; }

        /// <summary>
        /// Gets or sets the gateway address.
        /// </summary>
        public string? Gateway { get; set; }

        /// <summary>
        /// Gets or sets the subnet mask.
        /// </summary>
        public string? SubnetMask { get; set; }
    }
}
