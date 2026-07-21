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
        /// <para>
        /// Requires a USB control connection. Applying the LAN settings restarts the WiFi module,
        /// which would drop a WiFi/TCP control connection before the save step is delivered (leaving
        /// the new config applied-but-not-persisted), so this method throws
        /// <see cref="NetworkReconfigurationRequiresUsbException"/> when invoked over WiFi/TCP.
        /// </para>
        /// </remarks>
        /// <exception cref="NetworkReconfigurationRequiresUsbException">Thrown when the active control transport is not USB.</exception>
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
        /// Prepares the SD card interface for use. Over USB the LAN interface is disabled first to
        /// free the shared SPI bus for the SD card. Over WiFi/TCP the LAN is left enabled.
        /// </summary>
        /// <remarks>
        /// On older firmware the SD card and LAN interfaces share the same SPI bus and cannot be
        /// used simultaneously, so over USB the LAN is disabled before accessing the SD card. On
        /// firmware &gt;= v3.7.0 (#598/#599) the Harmony SPI driver arbitrates SD/WiFi transactions
        /// on the shared bus, so over a WiFi/TCP control transport the LAN MUST stay enabled (the
        /// SD reply routes back over that channel). Call this method before accessing the SD card.
        /// </remarks>
        void PrepareSdInterface();

        /// <summary>
        /// Prepares the LAN interface for use by disabling the SD card interface. Over USB the LAN
        /// is re-enabled; over WiFi/TCP the LAN (never disabled) is left untouched.
        /// </summary>
        /// <remarks>
        /// The mirror of <see cref="PrepareSdInterface"/> for restoring the interface after an SD
        /// card operation. Over USB it disables the SD subsystem and re-enables the LAN. Over a
        /// WiFi/TCP control transport the LAN was never disabled, so it is left alone (re-enabling
        /// it would re-initialize the WiFi module and drop the connection). Note: a
        /// network-reconfiguration flow that must unconditionally bring the LAN back up should
        /// enable it explicitly rather than rely on this transport-aware helper.
        /// </remarks>
        void PrepareLanInterface();
    }
}
