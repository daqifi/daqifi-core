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
    /// <summary>
    /// Upper bound on every await in this file. The collaborator's reads are unbounded by design —
    /// a live stream waits for the next sample — so a regression in cancellation, in delivery, or
    /// in argument validation would otherwise park a test forever and stall the whole run. Every
    /// wait here is bounded so such a regression surfaces as a fast <see cref="TimeoutException"/>
    /// instead. Generous enough not to flake on a loaded CI agent.
    /// </summary>
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

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
        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
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
        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));

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

        // Bounded on purpose: if cancellation ever stops ending the read, an unbounded await here
        // would park forever and hang the whole run. The timeout turns that into a fast, named
        // failure (TimeoutException instead of the expected OperationCanceledException).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => moveNext.AsTask().WaitAsync(MoveNextTimeout));
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
        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
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
        Assert.True(await firstMove.AsTask().WaitAsync(MoveNextTimeout));
        Assert.True(await secondMove.AsTask().WaitAsync(MoveNextTimeout));
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

        // Bounded on purpose: were the validation to stop throwing, this enumeration would block
        // on an empty buffer that nothing ever writes to, so an unbounded await would hang the run
        // rather than fail it.
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ConsumeAsync().WaitAsync(MoveNextTimeout));
        Assert.Equal("bufferCapacity", ex.ParamName);

        async Task ConsumeAsync()
        {
            await foreach (var _ in enumerable) { }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Issue #496: an enumeration is bound to the connected session it started in. What the device
    // observes end to end is pinned by DaqifiStreamingDeviceLiveStreamTerminationTests; these add
    // what only a direct test can see — the subscription bookkeeping on the new exit paths, several
    // enumerations at once, and the release that no status transition accompanies.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Enumeration_EndedByADrop_UnsubscribesFromEveryChannel()
    {
        var host = new FakeHost(new FakeChannel(0), new FakeChannel(1));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        stream.OnConnectionStatusChanged(ConnectionStatus.Lost);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => moveNext.AsTask().WaitAsync(MoveNextTimeout));

        // The issue's second criterion: the handlers went with the enumeration. A drop that ended
        // the loop but left it subscribed would still root the device for the rest of its life.
        Assert.All(host.Channels, c => Assert.Equal(0, c.SubscriberCount));
    }

    [Fact]
    public async Task Enumeration_EndedByADeliberateDisconnect_CompletesWithoutThrowing()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        stream.OnConnectionStatusChanged(ConnectionStatus.Disconnected);

        Assert.False(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(0, host.Channels[0].SubscriberCount);
    }

    [Fact]
    public async Task Enumeration_EndedByADrop_YieldsWhatWasBufferedFirst()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        host.Channels[0].RaiseSample(1.0);
        host.Channels[0].RaiseSample(2.0);

        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(1.0, e.Current.Sample.Value);

        stream.OnConnectionStatusChanged(ConnectionStatus.Lost);

        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(2.0, e.Current.Sample.Value);
        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
    }

    [Fact]
    public async Task Enumeration_ADropFollowedByTheTeardownItCauses_StillReportsTheDrop()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        // The real sequence after an unplug: Lost, then whatever teardown follows it — the reconnect
        // loop's Retrying, or the Disconnect a consumer issues from its status handler. None of them
        // may downgrade the drop to an ordinary end of session.
        stream.OnConnectionStatusChanged(ConnectionStatus.Lost);
        stream.OnConnectionStatusChanged(ConnectionStatus.Retrying);
        stream.OnConnectionStatusChanged(ConnectionStatus.Disconnected);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => moveNext.AsTask().WaitAsync(MoveNextTimeout));
    }

    [Fact]
    public async Task Enumeration_EndedByRelease_CompletesEvenWithNoStatusTransition()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        // Disposing a device that is already disconnected moves it nowhere, so the status hook never
        // fires. This is the path that keeps such a dispose from leaving the loop parked.
        stream.OnDeviceReleased();

        Assert.False(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(0, host.Channels[0].SubscriberCount);
    }

    [Fact]
    public async Task Enumeration_StartedAfterRelease_ThrowsWithIsShuttingDown()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);
        stream.OnDeviceReleased();

        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => ConsumeAsync().WaitAsync(MoveNextTimeout));

        // Latched, not momentary: a disposed device can never produce another sample, so this has to
        // keep failing rather than park.
        Assert.True(thrown.IsShuttingDown);
        Assert.Equal(0, host.SnapshotCalls);

        async Task ConsumeAsync()
        {
            await foreach (var _ in stream.StreamSamplesAsync(CancellationToken.None)) { }
        }
    }

    [Fact]
    public async Task Enumeration_StartedWhileDisconnected_ThrowsBeforeSubscribingToAnything()
    {
        var host = new FakeHost(new FakeChannel(0)) { IsConnected = false };
        var stream = new LiveSampleStream(host);

        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => ConsumeAsync().WaitAsync(MoveNextTimeout));

        Assert.False(thrown.IsShuttingDown);
        Assert.Equal(0, host.SnapshotCalls);
        Assert.Equal(0, host.Channels[0].SubscriberCount);

        async Task ConsumeAsync()
        {
            await foreach (var _ in stream.StreamSamplesAsync(CancellationToken.None)) { }
        }
    }

    [Fact]
    public async Task ConcurrentEnumerations_AllEndOnTheSameDrop()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var first = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var second = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var firstMove = first.MoveNextAsync();
        var secondMove = second.MoveNextAsync();

        stream.OnConnectionStatusChanged(ConnectionStatus.Lost);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => firstMove.AsTask().WaitAsync(MoveNextTimeout));
        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => secondMove.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(0, host.Channels[0].SubscriberCount);
    }

    [Fact]
    public async Task ADropAfterTheEnumerationHasEnded_IsANoOp()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        host.Channels[0].RaiseSample(1.0);
        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        await e.DisposeAsync();

        // A finished enumeration deregisters itself, so the device's own teardown — which always
        // follows eventually — has nothing left to touch.
        stream.OnConnectionStatusChanged(ConnectionStatus.Lost);
        stream.OnDeviceReleased();
        Assert.Equal(0, host.Channels[0].SubscriberCount);
    }

    [Fact]
    public async Task StayingConnected_DoesNotEndAnEnumeration()
    {
        var host = new FakeHost(new FakeChannel(0));
        var stream = new LiveSampleStream(host);

        await using var e = stream.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        // The transition a reconnect ends on. Only leaving Connected ends an enumeration; arriving
        // at it must leave a healthy one alone.
        stream.OnConnectionStatusChanged(ConnectionStatus.Connected);
        host.Channels[0].RaiseSample(4.0);

        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(4.0, e.Current.Sample.Value);
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

        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
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

        /// <summary>
        /// Bumped by <see cref="Add"/> so a caller caching a derivation of the channel set sees the
        /// change. <see cref="LiveSampleStream"/> does not cache, but the host contract says this
        /// moves whenever the channels do, and a fake that lied about it would let a future caller
        /// that <em>does</em> cache pass here and fail on a real device.
        /// </summary>
        public long ChannelStateVersion => _channels.Count;

        /// <summary>
        /// In the collaborator's remit since issue #496: an enumeration is only meaningful on a
        /// connected device, so the start-up guard reads this. Settable so both sides of that guard
        /// can be exercised. Reading state is still a world away from the members below, which
        /// <em>do</em> something to the device.
        /// </summary>
        public bool IsConnected { get; set; } = true;

        // Outside this block's remit — reaching for any of these is a regression, not a refinement.
        // In particular the live path must never send a command or take the channels lock: it is an
        // adapter over events the decoder already raises, and it runs on the decode thread.
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
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
