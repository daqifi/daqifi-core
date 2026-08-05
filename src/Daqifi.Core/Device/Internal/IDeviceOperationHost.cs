using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.SdCard;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The slice of a streaming device that its operation collaborators work through: the
    /// text-exchange and raw-capture primitives, the transport facts that change how a command must
    /// be issued, and the few pieces of device state those operations own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member here forwards to a member the device already had, and the ones that are
    /// <c>virtual</c> on the device stay virtual through this seam. That matters more than it
    /// looks: subclasses — instrumented devices in the field, and the test doubles that stand in
    /// for hardware — override <see cref="ExecuteTextCommandAsync"/>,
    /// <see cref="ExecuteRawCaptureAsync"/>, <see cref="Send"/> and <see cref="IsUsbConnection"/>
    /// to intercept device I/O. Routing the collaborators through the device's own virtual members
    /// keeps those overrides in the path; a collaborator that reached for the transport directly
    /// would silently step around every one of them.
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

        /// <inheritdoc cref="DaqifiStreamingDevice.IsUsbConnection"/>
        bool IsUsbConnection { get; }

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

        /// <inheritdoc cref="DaqifiDevice.Send{T}"/>
        void Send<T>(IOutboundMessage<T> message);

        /// <inheritdoc cref="DaqifiDevice.GetChannelsSnapshot"/>
        IReadOnlyList<IChannel> SnapshotChannels();

        /// <summary>
        /// Runs <paramref name="action"/> under the device's channels lock, so a collaborator that
        /// mutates <see cref="IChannel.IsEnabled"/> and derives outbound state from it does both in
        /// one critical section (#409). Blocking I/O — <see cref="Send{T}"/> — must stay outside it.
        /// </summary>
        void WithChannelsLock(Action action);

        /// <inheritdoc cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task})"/>
#pragma warning disable CA1068 // Matches the seam it forwards to, which orders these for source compatibility.
        Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null);
#pragma warning restore CA1068

        /// <inheritdoc cref="DaqifiDevice.ExecuteRawCaptureAsync"/>
        Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default);

        /// <inheritdoc cref="DaqifiDevice.EnsureSupported"/>
        void EnsureSupported(DeviceFeature feature);

        /// <inheritdoc cref="DaqifiDevice.CreateFeatureNotSupportedException"/>
        FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature);

        /// <summary>
        /// Overall wall-clock budget for one SD card download, read through the device so a
        /// subclass's override of it still applies.
        /// </summary>
        TimeSpan SdCardDownloadTimeout { get; }

        /// <summary>
        /// Inactivity window for an SD card transfer, read through the device so a subclass's
        /// override of it still applies.
        /// </summary>
        TimeSpan SdCardTransferIdleTimeout { get; }

        /// <summary>
        /// Raises the device's <see cref="DaqifiStreamingDevice.LowSdSpaceWarning"/> event.
        /// </summary>
        /// <remarks>
        /// Deliberately a call back into the device rather than an event the collaborator owns.
        /// The event is part of <see cref="ISdCardOperations"/>, so its <c>sender</c> has to remain
        /// the device a subscriber attached to — a collaborator raising it in its own name would be
        /// a silent, compile-clean behavior change.
        /// </remarks>
        void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e);
    }
}
