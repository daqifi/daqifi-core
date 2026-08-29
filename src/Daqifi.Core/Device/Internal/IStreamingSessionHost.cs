using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The slice of a streaming device that <see cref="StreamingSessionController"/> works through:
/// the connection guard, the outbound send, the sampling ceiling, the frame decoder's
/// per-session reset, and the channel operations a reconnect restore replays.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately its own seam rather than a facet of <see cref="IDeviceOperationHost"/>. That
/// interface already carries <c>IsStreaming</c>, <c>StreamingFrequency</c>,
/// <c>StartStreaming</c> and <c>StopStreaming</c> — the very members the controller now
/// implements — so routing the controller through it would fold the session state back onto
/// itself. The two seams point in opposite directions: the operation collaborators reach the
/// session through the device, and the session reaches the device's I/O through this.
/// </para>
/// <para>
/// Every member forwards to a member the device already had, and the ones that are
/// <c>virtual</c> on the device stay virtual through here — so a subclass that overrides
/// <see cref="Send{T}"/> (as the test doubles standing in for hardware do) still intercepts
/// every command the session sends. <see cref="DaqifiStreamingDevice"/> implements this
/// explicitly, so none of it widens the public API.
/// </para>
/// </remarks>
internal interface IStreamingSessionHost
{
    /// <inheritdoc cref="DaqifiDevice.EnsureConnected()"/>
    void EnsureConnected();

    /// <inheritdoc cref="DaqifiDevice.Send{T}"/>
    void Send<T>(IOutboundMessage<T> message);

    /// <summary>
    /// The device's advertised maximum sampling rate
    /// (<see cref="DeviceCapabilities.MaxSamplingRate"/>), read fresh on each
    /// access.
    /// </summary>
    /// <remarks>
    /// It is a mutable, unvalidated public property, so each caller reads it exactly once and
    /// validates against that single read — see
    /// <see cref="StreamingSessionController.TrackSessionCommand"/> for why reading it twice
    /// around one operation is a defect rather than a style choice.
    /// </remarks>
    int MaxSamplingRate { get; }

    /// <summary>
    /// Resets everything the frame decoder scopes to one streaming session — the timestamp
    /// anchor and tick frequency, the gap detector, the warmup guard, and the discard and
    /// decode-failure counters — for a session about to run at
    /// <paramref name="streamingFrequencyHz"/>.
    /// </summary>
    /// <remarks>
    /// The device supplies the tick frequency itself (<see cref="DaqifiDevice.TimestampFrequency"/>),
    /// because that is the device's own report of its clock and not something a session decides.
    /// </remarks>
    void BeginDecoderSession(int streamingFrequencyHz);

    /// <inheritdoc cref="IDeviceOperationHost.WithChannelsLock"/>
    void WithChannelsLock(Action action);

    /// <inheritdoc cref="DaqifiDevice.GetChannelsSnapshot"/>
    IReadOnlyList<IChannel> SnapshotChannels();

    /// <inheritdoc cref="DaqifiStreamingDevice.DisableAllChannels"/>
    void DisableAllChannels();

    /// <inheritdoc cref="DaqifiStreamingDevice.EnableChannels"/>
    void EnableChannels(IEnumerable<IChannel> channels);
}
