using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Network;

/// <summary>
/// Represents a device that supports network configuration.
/// </summary>
/// <remarks>
/// These members write the configuration. The settings the device reports are in
/// <see cref="DeviceMetadata.Ssid"/>, <see cref="DeviceMetadata.WifiInfrastructureMode"/> and
/// <see cref="DeviceMetadata.WifiSecurityMode"/>. They come from the device's last full status
/// message, so they do not change when a configuration is applied. The device never reports the
/// password.
/// </remarks>
public interface INetworkConfigurable
{
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
    /// <item><description>Enables the LAN interface</description></item>
    /// <item><description>Saves the configuration to persist across restarts</description></item>
    /// <item><description>Applies the LAN configuration, restarting the WiFi module</description></item>
    /// <item><description>Waits for the WiFi module to restart</description></item>
    /// </list>
    /// <para>
    /// The save deliberately precedes the apply. The device persists the staged settings rather
    /// than only the live ones, so saving first makes the new configuration durable before the
    /// applying restart can disturb the control connection. Applying last also means no command
    /// follows the restart, so none can be lost to it (#352).
    /// </para>
    /// <para>
    /// Cancellation applies only up to that save: past it the device has committed, so
    /// cancelling during the restart wait ends the wait early rather than failing the call.
    /// Reporting a cancellation there would tell the caller nothing happened while the device
    /// was in fact already sitting on the new configuration.
    /// </para>
    /// <para>
    /// <b>Over a WiFi/TCP control connection this is expected to drop the connection.</b>
    /// Restarting the WiFi module takes the device off the network carrying the control link
    /// whenever the new configuration points somewhere else — unavoidable when switching
    /// networks, and harmless here because the configuration is already saved. Callers should
    /// treat the device as disconnected once this method returns over WiFi and rediscover or
    /// reconnect on the new network, typically at a new address.
    /// </para>
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
    /// active connection.
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
    /// for the reset to take effect.
    /// </remarks>
    Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default);
}
