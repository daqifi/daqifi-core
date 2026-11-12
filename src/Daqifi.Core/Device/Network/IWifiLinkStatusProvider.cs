using System;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Network;

/// <summary>
/// Reads what a device's WiFi is actually doing — whether the link is up, and which mode the
/// radio is running — as opposed to the configuration it was given. Requires firmware v3.9.0 or
/// later (<see cref="DeviceFeature.WifiLinkStatus"/>).
/// </summary>
/// <remarks>
/// <para>
/// Before firmware v3.9.0 a client could read the <em>configured</em> WiFi mode but had no way to
/// tell whether applying it had worked: after <see cref="INetworkConfigurable.UpdateNetworkConfigurationAsync"/>
/// the only signal was elapsed time. These queries answer from the device's live state.
/// </para>
/// <para>
/// A separate interface rather than new members on <see cref="INetworkConfigurable"/>, so existing
/// implementers of that interface are not broken. <see cref="DaqifiStreamingDevice"/> implements
/// both. Check <see cref="DaqifiDevice.Supports"/> before calling, since older firmware is refused:
/// </para>
/// <code>
/// if (device is IWifiLinkStatusProvider wifi &amp;&amp; device.Supports(DeviceFeature.WifiLinkStatus))
/// {
///     var state = await wifi.GetWifiLinkStateAsync(cancellationToken);
/// }
/// </code>
/// <para>
/// Each query is a text exchange, which pauses decoding of streamed data while it runs; avoid
/// polling them during active streaming. Over a WiFi/TCP connection the answer travels on the very
/// link being reported, so these are most useful over USB — for example while a device is being
/// moved onto a new network.
/// </para>
/// </remarks>
public interface IWifiLinkStatusProvider
{
    /// <summary>
    /// Reads the live state of the device's WiFi link.
    /// </summary>
    /// <remarks>
    /// With WiFi disabled, or the device in standby, the firmware answers SCPI error <c>-200</c>
    /// instead of a state. That is reported as <see cref="WifiLinkState.Unavailable"/> — a value,
    /// not an exception — because it is an expected answer while a device is powered down or its
    /// LAN interface is off, not a failure of the query.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
    /// <returns>The link state the device reported.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or it does not report a version, or
    /// the device has no WiFi.
    /// </exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device refused the query with any SCPI error other than <c>-200</c>,
    /// answered with something that is not a known state, or did not answer.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task<WifiLinkState> GetWifiLinkStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the WiFi mode the device's radio is actually running, rather than the staged mode
    /// the next apply would bring up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> when no mode is up: WiFi is stopped, the radio is still starting, or
    /// the device is in standby. Unlike <see cref="GetWifiLinkStateAsync"/>, standby is not
    /// refused with <c>-200</c> here; the firmware simply reports that nothing is running.
    /// </para>
    /// <para>
    /// Two transient readings are known from the firmware (daqifi-nyquist-firmware #1186): a
    /// device joining an existing network reports <see cref="WifiMode.ExistingNetwork"/> while it
    /// keeps retrying a network it cannot associate with — it is configured and trying, so that is
    /// accurate — and a self-hosted access point reads <see cref="WifiMode.ExistingNetwork"/> for
    /// roughly the first 100 ms of its start-up. Pair this with <see cref="GetWifiLinkStateAsync"/>
    /// to tell "running" from "connected".
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
    /// <returns>The active WiFi mode, or <c>null</c> when no mode is up.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or it does not report a version, or
    /// the device has no WiFi. Older firmware does not take the argument that selects the active
    /// mode: it still answers with the <em>staged</em> mode and only then flags the argument
    /// (<c>-108</c>), so the query is refused up front rather than risk reporting the wrong one.
    /// </exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device refused the query with a SCPI error, answered with something that is
    /// not a known mode, or did not answer.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task<WifiMode?> GetActiveWifiModeAsync(CancellationToken cancellationToken = default);
}
