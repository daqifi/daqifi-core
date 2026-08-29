using Daqifi.Core.Channel;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The pull-based live-sample view of a streaming device — the bounded buffer behind
/// <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/> and the drop counter that goes with
/// it — extracted from <see cref="DaqifiStreamingDevice"/> (#344) so the device delegates
/// rather than hosts it.
/// </summary>
/// <remarks>
/// <para>
/// This is an adapter over the push-based <see cref="IChannel.SampleReceived"/> events, not a
/// second decode path: the decoder still raises those events exactly as before, and each
/// enumeration simply subscribes to them for its lifetime. Nothing here runs on the decode
/// thread beyond a non-blocking <see cref="ChannelWriter{T}.TryWrite"/>.
/// </para>
/// <para>
/// The channels to subscribe to are read through <see cref="IDeviceOperationHost"/>, so the
/// enumeration observes the same channel collection — under the same lock — that the rest of
/// the device does.
/// </para>
/// <para>
/// Every enumeration is bound to the connected session it started in (issue #496). Samples only
/// ever arrive while the device is connected, so an enumeration that outlived its session had
/// nothing left to wait for and waited anyway — forever, with no exception and no loop exit.
/// <see cref="OnConnectionStatusChanged"/> and <see cref="OnDeviceReleased"/> are the two ends
/// the device calls to close that out; see <see cref="StreamSamplesAsync"/> for the contract
/// they implement.
/// </para>
/// </remarks>
internal sealed class LiveSampleStream
{
    private readonly IDeviceOperationHost _host;

    /// <summary>
    /// Guards <see cref="_enumerations"/> and <see cref="_deviceReleased"/>. Held only for
    /// bookkeeping: an enumeration is always ended <em>outside</em> it, because completing a
    /// buffer can schedule the consumer's continuation, and the consumer's exit path takes this
    /// same lock to deregister itself.
    /// </summary>
    private readonly object _enumerationsLock = new object();

    /// <summary>
    /// The enumerations currently parked on this device, in start order. A live consumer is
    /// normally alone here; the list exists because nothing stops a caller from running several.
    /// </summary>
    private readonly List<Enumeration> _enumerations = new List<Enumeration>();

    /// <summary>
    /// Set once the device has released its resources. Latched rather than momentary: a disposed
    /// device can never produce another sample, so an enumeration started afterwards must fail
    /// rather than park.
    /// </summary>
    private bool _deviceReleased;

    /// <summary>
    /// Cumulative drop count across every enumeration this collaborator has served. Lives here
    /// rather than per-enumeration because the device exposes it as a device-wide health signal.
    /// </summary>
    private long _droppedSampleCount;

    /// <summary>
    /// Enumerations that have finished attaching their sample handlers and so are capturing.
    /// Distinct from <see cref="_enumerations"/>, which counts an enumeration from registration
    /// — before it can capture anything.
    /// </summary>
    private int _liveSubscriptionCount;

    internal LiveSampleStream(IDeviceOperationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.DroppedLiveSampleCount"/>
    internal long DroppedSampleCount => Interlocked.Read(ref _droppedSampleCount);

    /// <summary>
    /// Enumerations currently subscribed to the channels' sample events. Exists so a test can
    /// wait for a recording to actually be listening instead of sleeping for a guessed interval.
    /// </summary>
    internal int LiveSubscriptionCount => Volatile.Read(ref _liveSubscriptionCount);

    /// <summary>
    /// Ends every enumeration in flight when the device stops being connected.
    /// </summary>
    /// <remarks>
    /// Called by the device on every status transition, before consumers are notified. A
    /// transition to anything other than <see cref="ConnectionStatus.Connected"/> means no
    /// further samples can arrive on this session, so the enumerations are drained and closed:
    /// gracefully when the teardown was asked for (<see cref="ConnectionStatus.Disconnected"/>),
    /// and as a failure otherwise — an unplug reports <see cref="ConnectionStatus.Lost"/>, and a
    /// consumer that had its acquisition cut short must not mistake it for the end of the data.
    /// </remarks>
    /// <param name="status">The status the device has just moved to.</param>
    internal void OnConnectionStatusChanged(ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected)
        {
            return;
        }

        EndEnumerations(
            status == ConnectionStatus.Disconnected ? EndReason.Deliberate : EndReason.Dropped,
            release: false);
    }

    /// <summary>
    /// Ends every enumeration in flight, permanently, when the device releases its resources.
    /// </summary>
    /// <remarks>
    /// Disposal reaches here through <see cref="DaqifiDevice.Dispose()"/>'s disconnect in the
    /// normal case, which has already ended the enumerations by the time this runs. This is what
    /// covers the case that disconnect cannot: a device that was never connected — or was
    /// already disconnected — transitions nowhere on the way to being disposed, and an
    /// enumeration parked on it would otherwise never be told.
    /// </remarks>
    internal void OnDeviceReleased() => EndEnumerations(EndReason.Deliberate, release: true);

    /// <inheritdoc cref="DaqifiStreamingDevice.StreamSamplesAsync"/>
    internal async IAsyncEnumerable<LiveSample> StreamSamplesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int? bufferCapacity = null)
    {
        var capacity = bufferCapacity ?? DaqifiStreamingDevice.DefaultLiveSampleBufferCapacity;
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferCapacity), capacity, "Buffer capacity must be at least 1.");
        }

        var buffer = System.Threading.Channels.Channel.CreateBounded<LiveSample>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,

                // Explicit because ending an enumeration now happens on whichever thread
                // observed the drop — a transport watchdog, mid-teardown. Completing the buffer
                // must hand the consumer's continuation to the thread pool rather than run the
                // body of somebody's `await foreach` on that thread. This is the default; it is
                // spelled out so it cannot be flipped without reading this comment.
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _droppedSampleCount));

        var enumeration = new Enumeration(buffer.Writer);

        // Registered before anything is subscribed, so a teardown landing anywhere after this
        // point finds the enumeration and ends it. The connectivity guard shares the lock with
        // the teardown path, which is what makes "released, then registered" impossible.
        lock (_enumerationsLock)
        {
            if (_deviceReleased)
            {
                throw new DeviceNotConnectedException(
                    "The device has been disposed; live samples can no longer be enumerated.",
                    isShuttingDown: true);
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException(
                    "Device is not connected; live samples can only be enumerated on a connected device.");
            }

            _enumerations.Add(enumeration);
        }

        void OnSample(object? sender, SampleReceivedEventArgs e) =>
            buffer.Writer.TryWrite(new LiveSample(e.Channel, e.Sample));

        IReadOnlyList<IChannel> channels = Array.Empty<IChannel>();
        var subscribed = false;
        try
        {
            channels = _host.SnapshotChannels();
            foreach (var channel in channels)
            {
                channel.SampleReceived += OnSample;
            }

            // Published only after every handler is attached, so a test that waits on this is
            // waiting for the exact moment the enumeration can start capturing samples — not
            // for a guessed interval.
            Interlocked.Increment(ref _liveSubscriptionCount);
            subscribed = true;

            await foreach (var sample in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sample;
            }
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.SampleReceived -= OnSample;
            }

            if (subscribed)
            {
                Interlocked.Decrement(ref _liveSubscriptionCount);
            }

            buffer.Writer.TryComplete();

            lock (_enumerationsLock)
            {
                _enumerations.Remove(enumeration);
            }
        }

        // Reached only when the buffer completed and drained — a caller that breaks out of the
        // loop disposes the enumerator instead and never gets here. Everything already buffered
        // has been yielded by now, so the drop is reported after the last good sample rather
        // than in place of it.
        if (enumeration.Reason == EndReason.Dropped)
        {
            throw new DeviceNotConnectedException(
                "The device connection was lost while live samples were being enumerated.");
        }
    }

    /// <summary>
    /// Ends the enumerations currently registered, optionally latching the device as released.
    /// </summary>
    private void EndEnumerations(EndReason reason, bool release)
    {
        Enumeration[] ending;
        lock (_enumerationsLock)
        {
            if (release)
            {
                _deviceReleased = true;
            }

            if (_enumerations.Count == 0)
            {
                return;
            }

            ending = _enumerations.ToArray();
        }

        // Outside the lock on purpose: completing a buffer releases the consumer, whose exit
        // path deregisters itself under this same lock.
        foreach (var enumeration in ending)
        {
            enumeration.End(reason);
        }
    }

    /// <summary>
    /// Why an enumeration stopped, which is what decides whether the consumer's
    /// <c>await foreach</c> ends or throws.
    /// </summary>
    private enum EndReason
    {
        /// <summary>Still running, or ended by the consumer itself (cancellation, or a break).</summary>
        None = 0,

        /// <summary>The session was torn down on request — a disconnect, or a dispose.</summary>
        Deliberate,

        /// <summary>The connection went away underneath the enumeration.</summary>
        Dropped,
    }

    /// <summary>
    /// One in-flight enumeration, as the teardown paths see it: something to complete, plus the
    /// reason the consumer needs once it drains.
    /// </summary>
    private sealed class Enumeration
    {
        private readonly ChannelWriter<LiveSample> _writer;
        private int _reason;

        internal Enumeration(ChannelWriter<LiveSample> writer)
        {
            _writer = writer;
        }

        internal EndReason Reason => (EndReason)Volatile.Read(ref _reason);

        internal void End(EndReason reason)
        {
            // First reason wins. A drop is always followed by more transitions — the reconnect
            // loop's teardown, or the disconnect a consumer issues when it sees `Lost` — and
            // none of them may downgrade the unplug the consumer still has to be told about.
            Interlocked.CompareExchange(ref _reason, (int)reason, (int)EndReason.None);
            _writer.TryComplete();
        }
    }
}
