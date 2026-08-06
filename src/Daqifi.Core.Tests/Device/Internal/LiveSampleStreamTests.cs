using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="LiveSampleStream"/>, the pull-based live-sample view extracted from
/// <see cref="DaqifiStreamingDevice"/> (#344).
/// </summary>
/// <remarks>
/// <para>
/// What a consumer observes end to end — samples yielded in order, cancellation ending enumeration
/// without stopping the device, drop-oldest under backpressure, the deferred capacity throw — is
/// already pinned through the device by <c>DaqifiStreamingDeviceLiveStreamTests</c>. Those are
/// deliberately untouched: they are the evidence that the extraction changed nothing, so they are
/// not repeated here.
/// </para>
/// <para>
/// These add what only a direct test can see: the <b>subscription bookkeeping</b> (every channel
/// subscribed once at start and unsubscribed on every exit path, including cancellation), that the
/// channel set is snapshotted exactly once per enumeration, and that the drop counter is
/// device-wide rather than per-enumeration. The fake host below throws on every member outside
/// this block's remit, so a future change that sends a command, takes the channels lock, or
/// touches streaming state fails loudly rather than passing quietly.
/// </para>
/// </remarks>
public class LiveSampleStreamTests
{
    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LiveSampleStream(null!));
    }

    [Fact]
    public async Task Enumeration_SubscribesToEveryChannelInTheSnapshot()
    {
        var host = new FakeHost(new FakeChannel(0), new FakeChannel(1));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync(); // runs the body: subscribes synchronously, then awaits

        Assert.All(host.Channels, c => Assert.Equal(1, c.SubscriberCount));

        // Every subscribed channel must reach the same buffer, not just the first.
        host.Channels[1].RaiseSample(2.5);
        Assert.True(await moveNext.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(host.Channels[1], e.Current.Channel);
        Assert.Equal(2.5, e.Current.Sample.Value);
    }

    [Fact]
    public async Task Enumeration_Disposed_UnsubscribesFromEveryChannel()
    {
        var host = new FakeHost(new FakeChannel(0), new FakeChannel(1));
        var stream = new LiveSampleStream(host);

        var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        host.Channels[0].RaiseSample(1.0);
        Assert.True(await moveNext.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        await e.DisposeAsync();

        // A leaked handler would keep the decode path writing into a dead buffer for the rest of
        // the device's life — one per enumeration a consumer ever started.
        Assert.All(host.Channels, c => Assert.Equal(0, c.SubscriberCount));
    }

    [Fact]
    public async Task Enumeration_EndedByCancellation_StillUnsubscribes()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        using var cts = new CancellationTokenSource();
        var e = stream.StreamSamplesAsync(cts.Token).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext);
        await e.DisposeAsync();

        // The unsubscribe lives in a finally, so the throwing exit path has to clean up too.
        Assert.Equal(0, host.Channels[0].SubscriberCount);
    }

    [Fact]
    public async Task Enumeration_SnapshotsTheChannelsOnce_AndIgnoresLaterArrivals()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        Assert.Equal(1, host.SnapshotCalls);

        // A channel that appears after the enumeration started is not observed by it — that is the
        // documented "observes the channels present when it starts" contract, and it is also what
        // makes the unsubscribe list above exactly right.
        var late = new FakeChannel(1);
        host.Add(late);
        late.RaiseSample(9.0);
        Assert.Equal(0, late.SubscriberCount);

        host.Channels[0].RaiseSample(1.0);
        Assert.True(await moveNext.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1.0, e.Current.Sample.Value);
        Assert.Equal(1, host.SnapshotCalls);
    }

    [Fact]
    public async Task ConcurrentEnumerations_EachGetTheirOwnBuffer()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var first = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var second = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var firstMove = first.MoveNextAsync();
        var secondMove = second.MoveNextAsync();

        Assert.Equal(2, host.Channels[0].SubscriberCount);

        host.Channels[0].RaiseSample(4.0);

        // One sample, delivered to both consumers — neither steals it from the other.
        Assert.True(await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await secondMove.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4.0, first.Current.Sample.Value);
        Assert.Equal(4.0, second.Current.Sample.Value);
    }

    [Fact]
    public async Task DroppedSampleCount_AccumulatesAcrossEnumerations()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        Assert.Equal(0, stream.DroppedSampleCount);

        var afterFirst = await OverflowOnce(stream, host);
        Assert.True(afterFirst > 0, "drop-oldest should have dropped and counted overflow samples");

        var afterSecond = await OverflowOnce(stream, host);

        // The counter is a device-wide health signal, so a second enumeration adds to it rather
        // than starting over.
        Assert.True(afterSecond > afterFirst, $"expected the count to keep growing, got {afterFirst} then {afterSecond}");
    }

    [Fact]
    public async Task InvalidBufferCapacity_ThrowsOnFirstMoveNext_NotAtTheCall()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        // An async iterator defers its body, so nothing runs — and nothing is subscribed — until
        // the first MoveNextAsync. The device forwards this iterator as-is to keep that timing.
        var enumerable = stream.StreamSamplesAsync(CancellationToken.None, bufferCapacity: 0);
        Assert.Equal(0, host.SnapshotCalls);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in enumerable) { }
        });
        Assert.Equal("bufferCapacity", ex.ParamName);
    }

    #region Helpers

    /// <summary>
    /// Runs one enumeration whose reader is parked, floods it past its two-slot buffer, then
    /// returns the collaborator's drop count after that enumeration has been disposed.
    /// </summary>
    private static async Task<long> OverflowOnce(LiveSampleStream stream, FakeHost host)
    {
        var e = stream.StreamSamplesAsync(CancellationToken.None, bufferCapacity: 2).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync(); // subscribes; reader is awaiting, not consuming synchronously
        for (var i = 0; i < 20; i++)
        {
            host.Channels[0].RaiseSample(i);
        }

        Assert.True(await moveNext.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await e.DisposeAsync();
        return stream.DroppedSampleCount;
    }

    private sealed class FakeChannel : IChannel
    {
        private EventHandler<SampleReceivedEventArgs>? _sampleReceived;

        public FakeChannel(int channelNumber)
        {
            ChannelNumber = channelNumber;
            Name = "ch" + channelNumber;
        }

        public int SubscriberCount { get; private set; }

        public event EventHandler<SampleReceivedEventArgs>? SampleReceived
        {
            add { _sampleReceived += value; SubscriberCount++; }
            remove { _sampleReceived -= value; SubscriberCount--; }
        }

        public void RaiseSample(double value)
        {
            var sample = new DataSample(DateTime.UtcNow, value);
            ActiveSample = sample;
            _sampleReceived?.Invoke(this, new SampleReceivedEventArgs(this, sample));
        }

        public int ChannelNumber { get; }
        public string Name { get; set; }
        public bool IsEnabled { get; set; } = true;
        public ChannelType Type => ChannelType.Analog;
        public ChannelDirection Direction { get; set; } = ChannelDirection.Input;
        public IDataSample? ActiveSample { get; private set; }

        public void SetActiveSample(double value, DateTime timestamp) => throw new NotSupportedException();
        public void SetActiveSample(IDataSample sample) => throw new NotSupportedException();
    }

    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<FakeChannel> _channels;

        public FakeHost(params FakeChannel[] channels)
        {
            _channels = new List<FakeChannel>(channels);
        }

        public IReadOnlyList<FakeChannel> Channels => _channels;

        public int SnapshotCalls { get; private set; }

        public void Add(FakeChannel channel) => _channels.Add(channel);

        public IReadOnlyList<IChannel> SnapshotChannels()
        {
            SnapshotCalls++;
            return _channels.ToArray();
        }

        // Outside this block's remit — reaching for any of these is a regression, not a refinement.
        // In particular the live path must never send a command or take the channels lock: it is an
        // adapter over events the decoder already raises, and it runs on the decode thread.
        public bool IsConnected => throw new NotSupportedException();
        public bool IsUsbConnection => throw new NotSupportedException();
        public bool IsStreaming { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int StreamingFrequency => throw new NotSupportedException();
        public DeviceMetadata Metadata => throw new NotSupportedException();
        public TimeSpan SdCardDownloadTimeout => throw new NotSupportedException();
        public TimeSpan SdCardTransferIdleTimeout => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void Send<T>(IOutboundMessage<T> message) => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null) => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }

    #endregion
}
