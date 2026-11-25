using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;

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

    /// <summary>
    /// Starts the device's reader again when a raw capture left it stopped (issue #975).
    /// </summary>
    /// <remarks>
    /// See <see cref="DaqifiDevice.EnsureMessageReaderRunning"/>.
    /// </remarks>
    /// <exception cref="Daqifi.Core.Communication.Consumers.ConsumerThreadNotExitedException">The reader is stopped and cannot start.</exception>
    /// <exception cref="DeviceNotConnectedException">A teardown disposed the reader.</exception>
    void EnsureMessageReaderRunning();

    /// <inheritdoc cref="DaqifiDevice.SendsAreNeverHeldBack"/>
    bool SendsAreNeverHeldBack { get; }

    /// <inheritdoc cref="DaqifiDevice.SendsAreHeldBack"/>
    bool SendsAreHeldBack { get; }

    /// <inheritdoc cref="DaqifiDevice.PreserveActiveStream"/>
    bool PreserveActiveStream { get; }

    /// <inheritdoc cref="DaqifiDevice.Send{T}"/>
    void Send<T>(IOutboundMessage<T> message);

    /// <summary>
    /// Sends without holding back, for a command whose turn has come.
    /// </summary>
    void SendNow<T>(IOutboundMessage<T> message);

    /// <inheritdoc cref="DaqifiDevice.ReaderRunsForHeldBackSend"/>
    bool ReaderRunsForHeldBackStart(Action<Exception> onNotSent);

    /// <summary>
    /// The device's advertised maximum sampling rate
    /// (<see cref="DeviceCapabilities.MaxSamplingRate"/>), read fresh on each
    /// access.
    /// </summary>
    /// <remarks>
    /// It is a mutable, unvalidated public property, so each caller reads it exactly once and
    /// validates against that single read — see
    /// <see cref="StreamingSessionController.SendTracked"/> for why reading it twice
    /// around one operation is a defect rather than a style choice.
    /// </remarks>
    int MaxSamplingRate { get; }

    /// <summary>
    /// The highest rate the channel set that will be enabled when the next command goes out can
    /// sustain, read fresh on each access. The same as
    /// <see cref="DaqifiStreamingDevice.MaximumStreamingFrequencyHz"/>, except while an enable is
    /// held back.
    /// </summary>
    /// <remarks>
    /// This is the bound a start request is actually judged by, and it moves with the enabled
    /// set — unlike <see cref="MaxSamplingRate"/>, which is the board's absolute ceiling. The
    /// device answers a start above it with SCPI <c>-222</c> and streams nothing, so
    /// <see cref="StreamingSessionController.StartStreaming"/> reads it on the way in.
    /// </remarks>
    int MaximumStreamingFrequencyHz { get; }

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

    /// <inheritdoc cref="DaqifiDevice.Clock"/>
    TimeProvider Clock { get; }

    /// <summary>
    /// Ends a running SD log the way <see cref="DaqifiStreamingDevice.StopSdCardLoggingAsync"/>
    /// does, sending the same commands in the same order.
    /// </summary>
    void EndSdCardLogSession();

    /// <inheritdoc cref="ChannelControlOperations.IsEnableRequested"/>
    bool IsEnableRequested(IChannel channel);

    /// <summary>
    /// Makes a raw ADC enable mask the last one sent, so that it is recorded when it goes out and a
    /// typed enable builds on it meanwhile.
    /// </summary>
    ITrackedSend TrackAdcEnableMask(uint mask);

    /// <inheritdoc cref="DaqifiDevice.GetChannelsSnapshot"/>
    IReadOnlyList<IChannel> SnapshotChannels();

    /// <inheritdoc cref="DaqifiStreamingDevice.DisableAllChannels"/>
    void DisableAllChannels();

    /// <inheritdoc cref="DaqifiStreamingDevice.EnableChannels"/>
    void EnableChannels(IEnumerable<IChannel> channels);
}
