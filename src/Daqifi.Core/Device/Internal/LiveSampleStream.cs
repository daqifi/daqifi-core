using Daqifi.Core.Channel;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
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
    /// </remarks>
    internal sealed class LiveSampleStream
    {
        private readonly IDeviceOperationHost _host;

        /// <summary>
        /// Cumulative drop count across every enumeration this collaborator has served. Lives here
        /// rather than per-enumeration because the device exposes it as a device-wide health signal.
        /// </summary>
        private long _droppedSampleCount;

        internal LiveSampleStream(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="DaqifiStreamingDevice.DroppedLiveSampleCount"/>
        internal long DroppedSampleCount => Interlocked.Read(ref _droppedSampleCount);

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
                },
                _ => Interlocked.Increment(ref _droppedSampleCount));

            void OnSample(object? sender, SampleReceivedEventArgs e) =>
                buffer.Writer.TryWrite(new LiveSample(e.Channel, e.Sample));

            var channels = _host.SnapshotChannels();
            foreach (var channel in channels)
            {
                channel.SampleReceived += OnSample;
            }

            try
            {
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
                buffer.Writer.TryComplete();
            }
        }
    }
}
