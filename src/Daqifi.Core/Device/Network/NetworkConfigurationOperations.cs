using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;

#nullable enable

namespace Daqifi.Core.Device.Network
{
    /// <summary>
    /// The WiFi/LAN configuration half of <see cref="INetworkConfigurable"/>, extracted from
    /// <see cref="DaqifiStreamingDevice"/> so the device delegates rather than hosts it. Owns the
    /// last-known configuration the device reports.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <see cref="INetworkConfigurable"/> itself: that interface also carries
    /// <c>PrepareSdInterface</c> / <c>PrepareLanInterface</c>, which are the shared-SPI-bus handover
    /// every SD operation depends on and therefore belong with the SD operations. The device
    /// implements the interface and routes each member to whichever collaborator owns it.
    /// </remarks>
    internal sealed class NetworkConfigurationOperations
    {
        /// <summary>
        /// Delay after applying WiFi settings, in milliseconds, to allow the module to restart.
        /// </summary>
        private const int WIFI_MODULE_RESTART_DELAY_MS = 2000;

        private readonly IDeviceOperationHost _host;
        private readonly NetworkConfiguration _networkConfiguration = new NetworkConfiguration();

        internal NetworkConfigurationOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="INetworkConfigurable.NetworkConfiguration" />
        internal NetworkConfiguration NetworkConfiguration => _networkConfiguration.Clone();

        /// <inheritdoc cref="INetworkConfigurable.UpdateNetworkConfigurationAsync" />
        internal async Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            cancellationToken.ThrowIfCancellationRequested();

            _host.EnsureConnected();

            // Stop streaming if active
            if (_host.IsStreaming)
            {
                _host.StopStreaming();
            }

            // Set WiFi mode
            switch (configuration.Mode)
            {
                case WifiMode.ExistingNetwork:
                    _host.Send(ScpiMessageProducer.SetNetworkWifiModeExisting);
                    break;
                case WifiMode.SelfHosted:
                    _host.Send(ScpiMessageProducer.SetNetworkWifiModeSelfHosted);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.Mode, "Unsupported WiFi mode.");
            }

            // Set SSID
            _host.Send(ScpiMessageProducer.SetNetworkWifiSsid(configuration.Ssid));

            // Set security type and password
            switch (configuration.SecurityType)
            {
                case WifiSecurityType.None:
                    _host.Send(ScpiMessageProducer.SetNetworkWifiSecurityOpen);
                    break;
                case WifiSecurityType.WpaPskPhrase:
                    _host.Send(ScpiMessageProducer.SetNetworkWifiSecurityWpa);
                    _host.Send(ScpiMessageProducer.SetNetworkWifiPassword(configuration.Password));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.SecurityType, "Unsupported WiFi security type.");
            }

            // Stage static IP fields (firmware writes these into the runtime
            // WiFi settings that ApplyNetworkLan consumes). Skip any field the
            // caller left null so DHCP-only callers see no behavior change.
            if (configuration.StaticIP != null)
            {
                _host.Send(ScpiMessageProducer.SetLanAddress(configuration.StaticIP));
            }
            if (configuration.SubnetMask != null)
            {
                _host.Send(ScpiMessageProducer.SetLanMask(configuration.SubnetMask));
            }
            if (configuration.Gateway != null)
            {
                _host.Send(ScpiMessageProducer.SetLanGateway(configuration.Gateway));
            }

            // Stage the LAN interface state alongside the credentials above. LAN:ENAbled writes
            // isEnabled into the same runtime settings struct the SET commands populate and does
            // not restart anything itself, so it belongs before the save (to be persisted) and
            // before the apply (the firmware only fires a module REINIT when isEnabled is set).
            // This deliberately does NOT call PrepareLanInterface() — that is the transport-aware
            // SD-operation restore, which leaves the LAN alone over WiFi (where #598/#599 keep it
            // up). Here the LAN enable is unconditional: reconfiguration owns the LAN state.
            _host.Send(ScpiMessageProducer.DisableStorageSd);
            _host.Send(ScpiMessageProducer.EnableNetworkLan);

            // Cancellation boundary. This is the last point where abandoning still avoids the two
            // things that matter: nothing has been persisted (no LAN:SAVE) and no module restart
            // has been triggered (no LAN:APPLY), so the device keeps serving the network
            // configuration it already had. Past the save below it has committed, and cancellation
            // stops being a way out.
            //
            // This is deliberately NOT a side-effect-free point. The staged credentials, the LAN
            // enable flag and the SD disable above have all reached the device's runtime state, and
            // a later LAN:APPLY from any caller would pick up those staged values. No side-effect-
            // free abort exists once the sequence has begun — only the check at the top of this
            // method precedes every Send.
            cancellationToken.ThrowIfCancellationRequested();

            // Persist BEFORE applying (#352). LAN:SAVE copies the staged runtime settings straight
            // to NVM; it does NOT require them to have been applied first. Sending it here — while
            // the control link is still guaranteed alive — is what makes the reconfiguration
            // durable regardless of what the apply below does to the connection.
            _host.Send(ScpiMessageProducer.SaveNetworkLan);

            // Apply last: this restarts the WiFi module. Over a WiFi/TCP control connection that
            // restart necessarily tears down the link — inherent to moving the device onto a
            // different network, not a fault to be avoided. Because the save above already
            // committed the configuration to NVM, losing the link here costs nothing: the device
            // comes back on the new network with the settings intact. Nothing is sent after this
            // command, so there is no tail left to drop.
            _host.Send(ScpiMessageProducer.ApplyNetworkLan);

            // Hold for the module restart window before returning, so the apply is flushed to the
            // transport rather than left buffered in a connection that is about to go away.
            // Cancelling here ends the wait but does NOT fail the operation: the device has already
            // persisted and applied the new configuration, so reporting "canceled" — and skipping
            // the local-state update below — would leave the caller believing nothing happened
            // while the device is sitting on a different network.
            try
            {
                await Task.Delay(WIFI_MODULE_RESTART_DELAY_MS, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Already committed on the device; stop waiting early and complete normally.
            }

            // Update local configuration. Static IP fields use null = "leave
            // unchanged" semantics, so only overwrite when the caller provided
            // a value — otherwise we'd clobber the previously known static IP.
            _networkConfiguration.Mode = configuration.Mode;
            _networkConfiguration.SecurityType = configuration.SecurityType;
            _networkConfiguration.Ssid = configuration.Ssid;
            _networkConfiguration.Password = configuration.Password;
            if (configuration.StaticIP != null)
            {
                _networkConfiguration.StaticIP = configuration.StaticIP;
            }
            if (configuration.SubnetMask != null)
            {
                _networkConfiguration.SubnetMask = configuration.SubnetMask;
            }
            if (configuration.Gateway != null)
            {
                _networkConfiguration.Gateway = configuration.Gateway;
            }
        }

        /// <inheritdoc cref="INetworkConfigurable.LoadNetworkConfigurationAsync" />
        internal Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _host.EnsureConnected();

            // Re-check right before the state-changing send so a cancellation requested after the
            // entry guard still short-circuits the command (matches the pattern accepted in #324).
            cancellationToken.ThrowIfCancellationRequested();
            _host.Send(ScpiMessageProducer.LoadNetworkLan);
            return Task.CompletedTask;
        }

        /// <inheritdoc cref="INetworkConfigurable.FactoryResetNetworkAsync" />
        internal Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _host.EnsureConnected();

            // Re-check right before the state-changing send so a cancellation requested after the
            // entry guard still short-circuits the command (matches the pattern accepted in #324).
            cancellationToken.ThrowIfCancellationRequested();
            _host.Send(ScpiMessageProducer.FactoryResetNetworkLan);
            return Task.CompletedTask;
        }
    }
}
