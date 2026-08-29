using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The slice of a streaming device that its operation collaborators work through: the
/// text-exchange and raw-capture primitives, and the few pieces of device state those
/// operations own.
/// </summary>
/// <remarks>
/// <para>
/// Every member here forwards to a member the device already had, and the ones that are
/// <c>virtual</c> on the device stay virtual through this seam. That matters more than it
/// looks: subclasses — instrumented devices in the field, and the test doubles that stand in
/// for hardware — override <see cref="ExecuteTextCommandAsync"/>,
/// <see cref="ExecuteRawCaptureAsync"/> and <see cref="Send"/> to intercept device I/O.
/// Routing the collaborators through the device's own virtual members
/// keeps those overrides in the path; a collaborator that reached for the transport directly
/// would silently step around every one of them.
/// </para>
/// <para>
/// Kept to what <em>every</em> collaborator needs. A concern that belongs to one of them and to
/// a peripheral only some devices have — the SD card's transfer budgets, its low-space event,
/// and the USB-versus-network transport fact that only its shared-SPI-bus handling turns on —
/// lives on a facet that extends this seam (<see cref="SdCard.ISdCardOperationHost"/>) instead,
/// so channel control, administration, network configuration and diagnostics never have to see
/// it.
/// </para>
/// <para>
/// <see cref="DaqifiStreamingDevice"/> implements this explicitly, so none of it widens the
/// public API.
/// </para>
/// </remarks>
internal interface IDeviceOperationHost
{
    /// <inheritdoc cref="DaqifiDevice.IsConnected"/>
    bool IsConnected { get; }

    /// <inheritdoc cref="DaqifiStreamingDevice.IsStreaming"/>
    /// <remarks>
    /// Settable here because the SD operations defensively stop streaming before they touch the
    /// card and must record that they did (issue #118).
    /// </remarks>
    bool IsStreaming { get; set; }

    /// <inheritdoc cref="DaqifiStreamingDevice.StreamingFrequency"/>
    int StreamingFrequency { get; }

    /// <inheritdoc cref="DaqifiStreamingDevice.StopStreaming"/>
    void StopStreaming();

    /// <inheritdoc cref="DaqifiStreamingDevice.StartStreaming"/>
    /// <remarks>
    /// Used by the SD operations to resume a live stream their own defensive stop suspended, so
    /// the resume runs through the device's own session bookkeeping
    /// (<see cref="DaqifiStreamingDevice.StartStreaming"/> resets the frame decoder's session)
    /// rather than a raw <see cref="Send{T}"/> that would skip it.
    /// </remarks>
    void StartStreaming();

    /// <inheritdoc cref="DaqifiDevice.Send{T}"/>
    void Send<T>(IOutboundMessage<T> message);

    /// <inheritdoc cref="DaqifiDevice.Metadata"/>
    /// <remarks>
    /// The device's own metadata object, not a copy: the friendly-name write updates
    /// <see cref="DeviceMetadata.FriendlyName"/> optimistically on it, because the firmware does
    /// not echo the new name back and may not stream another status frame for a while.
    /// </remarks>
    DeviceMetadata Metadata { get; }

    /// <inheritdoc cref="DaqifiDevice.Disconnect"/>
    /// <remarks>
    /// Needed by the reboot command, which has to tear the local connection down after the
    /// device drops its link. Routed through the device so the whole disconnect path — lifecycle
    /// lock, message pumps, status event — runs exactly as it does for a caller-issued
    /// <see cref="DaqifiDevice.Disconnect"/>.
    /// </remarks>
    void Disconnect();

    /// <inheritdoc cref="DaqifiDevice.GetChannelsSnapshot"/>
    IReadOnlyList<IChannel> SnapshotChannels();

    /// <inheritdoc cref="DaqifiDevice.ChannelStateVersion"/>
    long ChannelStateVersion { get; }

    /// <summary>
    /// Runs <paramref name="action"/> under the device's channels lock, so a collaborator that
    /// mutates <see cref="IChannel.IsEnabled"/> and derives outbound state from it does both in
    /// one critical section (#409). Blocking I/O — <see cref="Send{T}"/> — must stay outside it.
    /// </summary>
    void WithChannelsLock(Action action);

    /// <inheritdoc cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>
#pragma warning disable CA1068 // Matches the seam it forwards to, which orders these for source compatibility.
    Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
        Action setupAction,
        int responseTimeoutMs = 1000,
        int completionTimeoutMs = 250,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? prepareAsync = null,
        Func<Task>? finalizeAsync = null,
        bool keepBlankLines = false);
#pragma warning restore CA1068

    /// <inheritdoc cref="DaqifiDevice.DrainErrorQueueAsync"/>
    /// <remarks>
    /// Needed by the confirming administration commands, which drain the queue before they send so
    /// the entry they pop afterwards is their own command's and not an older one's.
    /// </remarks>
    Task<IReadOnlyList<string>> DrainErrorQueueAsync(
        int maxIterations = 256,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="DaqifiDevice.ExecuteRawCaptureAsync"/>
    Task ExecuteRawCaptureAsync(
        Func<Stream, CancellationToken, Task> rawAction,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="DaqifiDevice.EnsureSupported"/>
    void EnsureSupported(DeviceFeature feature);

    /// <inheritdoc cref="DaqifiDevice.CreateFeatureNotSupportedException"/>
    FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature);

    /// <summary>
    /// Raises the device's <see cref="DaqifiStreamingDevice.StreamFrameDiscarded"/> event,
    /// isolating a throwing subscriber.
    /// </summary>
    /// <remarks>
    /// Deliberately a call back into the device rather than an event the collaborator owns: the
    /// event is part of the device's public surface, so its <c>sender</c> must stay the device a
    /// subscriber attached to — a collaborator raising it in its own name would be a silent,
    /// compile-clean behavior change.
    /// </remarks>
    void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e);

    /// <summary>
    /// Raises the device's <see cref="DaqifiStreamingDevice.GapDetected"/> event, isolating a
    /// throwing subscriber so it cannot skip the rest of the frame's decode.
    /// </summary>
    void RaiseGapDetected(TimestampGapEventArgs e);

    /// <summary>
    /// Re-raises a streaming frame for raw-frame consumers, through the device's base
    /// <see cref="DaqifiDevice.OnStreamMessageReceived"/>.
    /// </summary>
    /// <remarks>
    /// This is the <c>MessageReceived</c> path most callers actually use. It has to run through
    /// the device rather than from a collaborator so the base implementation — and any subclass
    /// sitting between it and the streaming device — still sees the frame.
    /// </remarks>
    void RaiseRawStreamFrame(DaqifiOutMessage message);

    /// <summary>
    /// Reports a frame whose decode threw through the device's
    /// <see cref="DaqifiDevice.ErrorOccurred"/> surface as
    /// <see cref="DeviceErrorSource.StreamDecode"/> (issue #378).
    /// </summary>
    /// <remarks>
    /// Observation only — the frame is dropped either way, and the device's throttling decides
    /// whether the event is actually raised.
    /// </remarks>
    void RaiseStreamDecodeFailure(Exception error);
}
