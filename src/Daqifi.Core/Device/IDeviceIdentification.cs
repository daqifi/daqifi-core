using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Producers;

namespace Daqifi.Core.Device;

/// <summary>
/// Tells one physical board from another: a name stored on the device, and its LEDs.
/// </summary>
/// <remarks>
/// A capability interface, like <see cref="Network.INetworkConfigurable"/>, rather than members of
/// <see cref="IStreamingDevice"/>: an <c>...Async</c> command there completes once the device has
/// confirmed it, and the firmware acknowledges neither write here. <see cref="DaqifiStreamingDevice"/>
/// implements it, so every device <see cref="DaqifiDeviceFactory"/> connects does.
/// </remarks>
public interface IDeviceIdentification
{
    /// <summary>
    /// Sets and persists the device's user-defined friendly name to NVM, then optimistically
    /// updates <see cref="IStreamingDevice.Metadata"/>'s <see cref="DeviceMetadata.FriendlyName"/>.
    /// </summary>
    /// <remarks>
    /// Composes the firmware sequence <c>SYSTem:DEVice:NAME "name"</c> then
    /// <c>SYSTem:DEVice:NAME:SAVE</c> (producer commands added in #302). The device does not echo
    /// the new name back synchronously — and may not stream another status frame for a while — so
    /// the local metadata is updated optimistically once both commands are sent.
    ///
    /// <para>Completion semantics: the returned task completes once the commands are enqueued to
    /// the outbound producer — it does <b>not</b> await on-device application or NVM persistence,
    /// which the firmware does not acknowledge. This matches the other fire-and-forget device
    /// commands (e.g. <see cref="Network.INetworkConfigurable.LoadNetworkConfigurationAsync"/>, <see cref="Network.INetworkConfigurable.FactoryResetNetworkAsync"/>);
    /// the async signature exists for cancellation and device-surface consistency.</para>
    /// </remarks>
    /// <param name="name">
    /// 1-<see cref="ScpiMessageProducer.MaximumFriendlyNameLength"/> printable ASCII characters
    /// (0x20-0x7E), excluding <c>"</c> and <c>\</c> — see <see cref="ScpiMessageProducer.IsFriendlyNameValid"/>.
    /// </param>
    /// <param name="cancellationToken">A cancellation token observed before the commands are sent.</param>
    /// <returns>A task that completes once both commands have been sent.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> fails validation.</exception>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetFriendlyNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or stops flashing both of the device's LEDs, so the user can tell which physical
    /// board this connection is talking to (<c>SYSTem:IDENtify 1</c> / <c>0</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// While on, the LEDs repeat a pattern of about one second of fast flashing followed by about
    /// one second off, and that pattern replaces every other LED indication (power, charging,
    /// streaming, errors) until it is turned off. The firmware puts <b>no time limit</b> on it:
    /// it flashes until <c>SetIdentifyAsync(false)</c> or a reboot, so a caller that starts it
    /// owns turning it off. Disconnecting does not stop it.
    /// </para>
    /// <para>
    /// A single write with no reply, like <see cref="SetFriendlyNameAsync"/>: the task completes
    /// once the command is sent, and it is safe to call while streaming. Use
    /// <see cref="GetIdentifyAsync"/> to read the state back.
    /// </para>
    /// <para>Requires firmware v3.9.0 or newer (<see cref="DeviceFeature.Identify"/>).</para>
    /// </remarks>
    /// <param name="enabled"><c>true</c> to start flashing, <c>false</c> to stop.</param>
    /// <param name="cancellationToken">A cancellation token observed before the command is sent.</param>
    /// <returns>A task that completes once the command has been sent.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or its version is unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetIdentifyAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the device whether it is currently flashing its LEDs to identify itself
    /// (<c>SYSTem:IDENtify?</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the device's own flag, so it also reports an identify started by another
    /// connection or an earlier session.
    /// </para>
    /// <para>
    /// This runs a text-mode exchange, which stops decoding protobuf frames for its duration.
    /// Do not call it while streaming.
    /// </para>
    /// <para>Requires firmware v3.9.0 or newer (<see cref="DeviceFeature.Identify"/>).</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns><c>true</c> while the device is identifying; otherwise <c>false</c>.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or its version is unknown — or when
    /// the device itself answers the query with SCPI <c>-113</c> "Undefined header", whatever
    /// version it reported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device rejected the query with any other error, or answered with something
    /// other than <c>1</c> or <c>0</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task<bool> GetIdentifyAsync(CancellationToken cancellationToken = default);
}
