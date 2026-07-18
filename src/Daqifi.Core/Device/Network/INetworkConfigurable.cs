using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// Represents a device that supports network configuration.
    /// </summary>
    public interface INetworkConfigurable
    {
        /// <summary>
        /// Gets the current network configuration.
        /// </summary>
        NetworkConfiguration NetworkConfiguration { get; }

        /// <summary>
        /// Updates the device network configuration with the specified settings.
        /// </summary>
        /// <param name="configuration">The new network configuration to apply.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <remarks>
        /// <para>
        /// This method performs the following steps:
        /// </para>
        /// <list type="number">
        /// <item><description>Stops any active streaming</description></item>
        /// <item><description>Sends WiFi mode configuration command</description></item>
        /// <item><description>Sets the SSID</description></item>
        /// <item><description>Sets the security type</description></item>
        /// <item><description>Sets the password (if security is enabled)</description></item>
        /// <item><description>Sets the static IP, subnet mask, and gateway (only those provided as non-null)</description></item>
        /// <item><description>Applies the LAN configuration</description></item>
        /// <item><description>Waits for the WiFi module to restart</description></item>
        /// <item><description>Re-enables the LAN interface</description></item>
        /// <item><description>Saves the configuration to persist across restarts</description></item>
        /// </list>
        /// <para>
        /// Note: The SD card and LAN interfaces share the same SPI bus on the device hardware
        /// and cannot be used simultaneously. This method handles the interface switching automatically.
        /// </para>
        /// </remarks>
        Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the persisted LAN configuration from the device's NVM back into its runtime settings.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <remarks>
        /// The inverse of the save step performed by <see cref="UpdateNetworkConfigurationAsync"/>. This is a
        /// thin wrapper over the firmware NVM primitive (<c>SYSTem:COMMunicate:LAN:LOAD</c>): it repopulates
        /// the device's runtime WiFi settings from the last saved values but does not itself re-apply them to
        /// the live interface — send an apply/reboot afterwards if the loaded values must take effect on the
        /// active connection. The local <see cref="NetworkConfiguration"/> snapshot is not refreshed.
        /// </remarks>
        Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Resets the device's LAN configuration to firmware factory defaults.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <remarks>
        /// A thin wrapper over the firmware NVM primitive (<c>SYSTem:COMMunicate:LAN:FACRESET</c>): it restores
        /// the default WiFi settings into the device's runtime settings. Persist and/or apply them afterwards
        /// for the reset to take effect. The local <see cref="NetworkConfiguration"/> snapshot is not refreshed.
        /// </remarks>
        Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Prepares the SD card interface for use by disabling the LAN interface.
        /// </summary>
        /// <remarks>
        /// The SD card and LAN interfaces share the same SPI bus on the device hardware
        /// and cannot be used simultaneously. Call this method before accessing the SD card.
        /// </remarks>
        void PrepareSdInterface();

        /// <summary>
        /// Prepares the LAN interface for use by disabling the SD card interface.
        /// </summary>
        /// <remarks>
        /// The SD card and LAN interfaces share the same SPI bus on the device hardware
        /// and cannot be used simultaneously. Call this method before using network communication.
        /// </remarks>
        void PrepareLanInterface();
    }
}
