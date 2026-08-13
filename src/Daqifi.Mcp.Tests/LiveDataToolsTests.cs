using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the live-data tools (#498) — <c>read_channel_values</c> and
/// <c>capture_samples</c>, the pair that finally lets an agent read a measurement instead of only
/// configuring one.
/// </summary>
/// <remarks>
/// No device is attached here. What these pin is everything the tools decide around the wire: what
/// a caller is told when nothing is connected, how a caller's budget is clamped, and — the part
/// that actually shapes the data — how a stream of per-channel samples becomes the latest value
/// per channel or a table of timestamp-aligned rows.
/// </remarks>
public class LiveDataAgentGuardTests
{
    private static DaqifiAgent NewAgent(bool readOnly = false) =>
        new(new ServerOptions { ReadOnly = readOnly });

    [Fact]
    public async Task ReadChannelValues_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().ReadChannelValuesAsync("serial:NOPE", 2000, CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task CaptureSamples_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().CaptureSamplesAsync("serial:NOPE", 1000, 500, CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    // Reading a stream that is already running changes nothing on the device, so --read-only is
    // not a blanket refusal for these two the way it is for delete_sd_file: the refusal only
    // applies when the call would have to START the stream, which cannot be known before the
    // device is in hand. These fail for want of a device, never for want of permission.
    [Theory]
    [InlineData("read")]
    [InlineData("capture")]
    public async Task LiveTools_InReadOnlyMode_AreNotRefusedBeforeTheDeviceIsEvenKnown(string operation)
    {
        var agent = NewAgent(readOnly: true);

        Task Call() => operation == "read"
            ? agent.ReadChannelValuesAsync("serial:NOPE", 2000, CancellationToken.None)
            : agent.CaptureSamplesAsync("serial:NOPE", 1000, 500, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);
        Assert.DoesNotContain("read-only", ex.Message);
        Assert.Contains("not connected", ex.Message);
    }

    // The device is streaming already: nothing has to be started, so nothing is refused, and the
    // caller does not own the stream (it must be left running when the read is done).
    [Fact]
    public void StreamAlreadyRunning_IsReadWithoutBeingOwned_EvenInReadOnlyMode()
    {
        Assert.False(DaqifiAgent.DecideStreamOwnership(isStreaming: true, readOnly: false));
        Assert.False(DaqifiAgent.DecideStreamOwnership(isStreaming: true, readOnly: true));
    }

    [Fact]
    public void IdleDevice_IsStartedByTheCaller_WhoThenOwnsTheStream()
    {
        Assert.True(DaqifiAgent.DecideStreamOwnership(isStreaming: false, readOnly: false));
    }

    [Fact]
    public void IdleDeviceInReadOnlyMode_IsRefused_AndSaysWhyItCouldStillWork()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DaqifiAgent.DecideStreamOwnership(isStreaming: false, readOnly: true));

        Assert.Contains("read-only", ex.Message);
        Assert.Contains("already running", ex.Message);
    }

    [Theory]
    [InlineData(0, DaqifiAgent.MinReadTimeoutMs)]
    [InlineData(-5, DaqifiAgent.MinReadTimeoutMs)]
    [InlineData(2000, 2000)]
    [InlineData(int.MaxValue, DaqifiAgent.MaxReadTimeoutMs)]
    public void ReadTimeout_IsClampedIntoTheSupportedRange(int requestedMs, int expectedMs)
    {
        var window = DaqifiAgent.ClampLiveWindow(
            requestedMs, DaqifiAgent.MinReadTimeoutMs, DaqifiAgent.MaxReadTimeoutMs);
        Assert.Equal(expectedMs, window.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0, DaqifiAgent.MinCaptureDurationMs)]
    [InlineData(1000, 1000)]
    [InlineData(600_000, DaqifiAgent.MaxCaptureDurationMs)]
    public void CaptureDuration_IsClampedIntoTheSupportedRange(int requestedMs, int expectedMs)
    {
        var window = DaqifiAgent.ClampLiveWindow(
            requestedMs, DaqifiAgent.MinCaptureDurationMs, DaqifiAgent.MaxCaptureDurationMs);
        Assert.Equal(expectedMs, window.TotalMilliseconds);
    }
}

/// <summary>
/// Tests for the driver that reads Core's live stream into a sink for a bounded window.
/// </summary>
public class LiveSampleCaptureTests
{
    /// <summary>
    /// Upper bound on every wait in this file. The drain reads an unbounded stream by design, so a
    /// regression in its stop conditions would otherwise park the run forever; bounded waits turn
    /// that into a fast, named failure instead.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // The device stream must not be started until the enumeration is subscribed, or the first
    // samples of a capture are decoded while nothing is listening. Core subscribes in the
    // synchronous prefix of its iterator body, which is what makes this orderable at all.
    [Fact]
    public async Task DrainAsync_StartsTheStreamOnlyAfterTheEnumerationHasSubscribed()
    {
        var order = new List<string>();
        var samples = Channel.CreateUnbounded<LiveSample>();
        var stream = Stream(samples.Reader, onSubscribe: () => order.Add("subscribed"));
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        var drain = LiveSampleCapture.DrainAsync(
            stream, sink, TimeSpan.FromSeconds(5), () => order.Add("stream started"), CancellationToken.None);

        samples.Writer.TryWrite(Sample(ChannelType.Analog, 0, 1.5));
        await drain.WaitAsync(Bound);

        Assert.Equal(new[] { "subscribed", "stream started" }, order);
    }

    [Fact]
    public async Task DrainAsync_ReturnsAsSoonAsTheSinkIsComplete_WithoutWaitingOutTheWindow()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0), Key(ChannelType.Digital, 1) });

        samples.Writer.TryWrite(Sample(ChannelType.Analog, 0, 1.5));
        samples.Writer.TryWrite(Sample(ChannelType.Digital, 1, 1));
        samples.Writer.TryWrite(Sample(ChannelType.Analog, 0, 1.6));

        // A window far longer than the test could afford to wait for: completing early is the only
        // way this returns in time.
        var outcome = await LiveSampleCapture
            .DrainAsync(Stream(samples.Reader), sink, TimeSpan.FromMinutes(5), null, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(2, outcome.SampleCount);
        Assert.True(sink.IsComplete);
    }

    // A capture that has to start the device's stream sees nothing for its first ~100 ms. Timing
    // the data from the first sample rather than from the call is what stops that wait from being
    // charged to the device as a lower measured rate — badly, on a short capture.
    [Fact]
    public async Task DrainAsync_TimesTheDataSeparatelyFromTheWaitForItToStart()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1) });

        var drain = LiveSampleCapture.DrainAsync(
            Stream(samples.Reader), sink, TimeSpan.FromSeconds(30), null, CancellationToken.None);

        await Task.Delay(300);
        samples.Writer.TryWrite(Sample(ChannelType.Analog, 0, 1.0));
        samples.Writer.TryWrite(Sample(ChannelType.Analog, 1, 2.0));

        var outcome = await drain.WaitAsync(Bound);

        Assert.True(outcome.Elapsed >= TimeSpan.FromMilliseconds(250), $"elapsed was {outcome.Elapsed}");
        Assert.True(
            outcome.DataElapsed < outcome.Elapsed - TimeSpan.FromMilliseconds(100),
            $"the 300 ms wait for the first sample was charged to the data: elapsed {outcome.Elapsed}, data {outcome.DataElapsed}");
    }

    [Fact]
    public async Task DrainAsync_WindowElapsesWithNoSamples_ReturnsEmptyRatherThanThrowing()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        var outcome = await LiveSampleCapture
            .DrainAsync(Stream(samples.Reader), sink, TimeSpan.FromMilliseconds(50), null, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(0, outcome.SampleCount);
        Assert.False(sink.IsComplete);
    }

    [Fact]
    public async Task DrainAsync_StreamEnds_ReturnsWhatItHad()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1) });

        samples.Writer.TryWrite(Sample(ChannelType.Analog, 0, 1.5));
        samples.Writer.Complete();

        var outcome = await LiveSampleCapture
            .DrainAsync(Stream(samples.Reader), sink, TimeSpan.FromMinutes(5), null, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(1, outcome.SampleCount);
        Assert.False(sink.IsComplete); // AI1 never reported, and the stream is over
    }

    // The window ending is how a timed capture finishes; the caller's own cancellation is a
    // different thing and has to reach the caller.
    [Fact]
    public async Task DrainAsync_CallerCancels_Propagates()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });
        using var cts = new CancellationTokenSource();

        var drain = LiveSampleCapture.DrainAsync(
            Stream(samples.Reader), sink, TimeSpan.FromMinutes(5), null, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(Bound));
    }

    // A device that drops out mid-capture must not look like a capture that simply ended.
    [Fact]
    public async Task DrainAsync_StreamFails_SurfacesTheFailure()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => LiveSampleCapture
                .DrainAsync(FailingStream(), sink, TimeSpan.FromMinutes(5), null, CancellationToken.None)
                .WaitAsync(Bound));
    }

    // Starting the stream can fail (an unplug between connecting and reading). The enumeration is
    // already in flight at that point, and disposing an async iterator mid-read throws
    // NotSupportedException — which would replace the failure the caller needs with a meaningless one.
    [Fact]
    public async Task DrainAsync_StartingTheStreamFails_SurfacesThatFailure_NotADisposalOne()
    {
        var samples = Channel.CreateUnbounded<LiveSample>();
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => LiveSampleCapture
                .DrainAsync(
                    Stream(samples.Reader),
                    sink,
                    TimeSpan.FromMinutes(5),
                    () => throw new DeviceNotConnectedException(),
                    CancellationToken.None)
                .WaitAsync(Bound));

        Assert.IsType<DeviceNotConnectedException>(ex);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Stands in for Core's live stream, and deliberately has the same shape: an async iterator
    /// whose subscription runs in the synchronous prefix of the body, before the first await.
    /// </summary>
    private static async IAsyncEnumerable<LiveSample> Stream(
        ChannelReader<LiveSample> reader,
        Action? onSubscribe = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        onSubscribe?.Invoke();

        await foreach (var sample in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    private static async IAsyncEnumerable<LiveSample> FailingStream()
    {
        await Task.Yield();
        throw new DeviceNotConnectedException();
#pragma warning disable CS0162 // Unreachable: an iterator needs a yield to be one at all.
        yield break;
#pragma warning restore CS0162
    }

    internal static ChannelKey Key(ChannelType type, int number) => new(type, number);

    internal static LiveSample Sample(ChannelType type, int number, double value, DateTime? timestamp = null, uint? deviceTimestamp = null)
    {
        IChannel channel = type == ChannelType.Analog
            ? new AnalogChannel(number)
            : new DigitalChannel(number);

        return new LiveSample(
            channel,
            new DataSample(timestamp ?? new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Local), value, rawValue: null, deviceTimestamp));
    }
}

/// <summary>
/// Tests for the sink behind <c>read_channel_values</c>: the latest sample per channel.
/// </summary>
public class LatestValueSinkTests
{
    [Fact]
    public void KeepsTheMostRecentValuePerChannel()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        sink.Add(Sample(ChannelType.Analog, 0, 1.0));
        sink.Add(Sample(ChannelType.Analog, 0, 2.0));

        Assert.Equal(2.0, sink.Latest(Key(ChannelType.Analog, 0))!.Value);
    }

    // A device numbers its analog inputs and its digital pins from 0 independently, so a sink keyed
    // by number alone would have DIO0's 0/1 overwrite AI0's volts.
    [Fact]
    public void AnalogAndDigitalChannelsWithTheSameNumber_AreDifferentChannels()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0), Key(ChannelType.Digital, 0) });

        sink.Add(Sample(ChannelType.Analog, 0, 4.5));
        sink.Add(Sample(ChannelType.Digital, 0, 1));

        Assert.Equal(4.5, sink.Latest(Key(ChannelType.Analog, 0))!.Value);
        Assert.Equal(1, sink.Latest(Key(ChannelType.Digital, 0))!.Value);
        Assert.Equal(2, sink.ReportedChannelCount);
    }

    [Fact]
    public void IsComplete_OnlyOnceEveryExpectedChannelHasReported()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1) });

        sink.Add(Sample(ChannelType.Analog, 0, 1.0));
        Assert.False(sink.IsComplete);

        sink.Add(Sample(ChannelType.Analog, 1, 1.0));
        Assert.True(sink.IsComplete);
    }

    [Fact]
    public void ChannelNeverReported_ReadsAsNoDataRatherThanZero()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        Assert.Null(sink.Latest(Key(ChannelType.Analog, 0)));
        Assert.Equal(0, sink.ReportedChannelCount);
    }

    // Someone enabling a channel while the read runs: it has no slot in the answer the caller
    // asked for, so it is counted and reported rather than quietly dropped.
    [Fact]
    public void SampleForAnUnexpectedChannel_IsCountedNotFiled()
    {
        var sink = new LatestValueSink(new[] { Key(ChannelType.Analog, 0) });

        sink.Add(Sample(ChannelType.Analog, 7, 1.0));

        Assert.Equal(1, sink.UnexpectedSampleCount);
        Assert.Equal(0, sink.ReportedChannelCount);
        Assert.False(sink.IsComplete);
    }

    private static ChannelKey Key(ChannelType type, int number) => LiveSampleCaptureTests.Key(type, number);

    private static LiveSample Sample(ChannelType type, int number, double value) =>
        LiveSampleCaptureTests.Sample(type, number, value);
}

/// <summary>
/// Tests for the sink behind <c>capture_samples</c>: per-channel samples grouped into
/// timestamp-aligned rows.
/// </summary>
public class SampleRowSinkTests
{
    private static readonly DateTime T0 = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void SamplesSharingATimestamp_BecomeOneRowInColumnOrder()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1));

        sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0));
        sink.Add(Sample(ChannelType.Analog, 1, 2.0, T0));

        var rows = sink.Complete();
        Assert.Single(rows);
        Assert.Equal(new double?[] { 1.0, 2.0 }, rows[0].Values);
        Assert.Equal(T0, rows[0].Timestamp);
        Assert.Equal(2, sink.SampleCount);
    }

    [Fact]
    public void ANewTimestamp_StartsANewRow()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0));

        sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0));
        sink.Add(Sample(ChannelType.Analog, 0, 2.0, T0.AddMilliseconds(1)));

        var rows = sink.Complete();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1.0, rows[0].Values[0]);
        Assert.Equal(2.0, rows[1].Values[0]);
    }

    // Firmware 3.7.2 hands consecutive frames the same timestamp at high sample rates. Grouping on
    // the timestamp alone would let the second frame's value overwrite the first's and lose a
    // sample; a channel reporting twice is itself the signal that a new tick has started.
    [Fact]
    public void AChannelReportingTwiceUnderOneTimestamp_StartsANewRowInsteadOfOverwriting()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1));

        sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0));
        sink.Add(Sample(ChannelType.Analog, 1, 2.0, T0));
        sink.Add(Sample(ChannelType.Analog, 0, 3.0, T0));
        sink.Add(Sample(ChannelType.Analog, 1, 4.0, T0));

        var rows = sink.Complete();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new double?[] { 1.0, 2.0 }, rows[0].Values);
        Assert.Equal(new double?[] { 3.0, 4.0 }, rows[1].Values);
    }

    [Fact]
    public void AChannelMissingFromATick_LeavesItsColumnEmptyRatherThanZero()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1));

        sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0));

        var rows = sink.Complete();
        Assert.Single(rows);
        Assert.Equal(1.0, rows[0].Values[0]);
        Assert.Null(rows[0].Values[1]);
    }

    [Fact]
    public void RowBudgetReached_StopsTheCapture()
    {
        var sink = NewSink(maxRows: 2, Key(ChannelType.Analog, 0));

        Assert.True(sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0)));
        Assert.True(sink.Add(Sample(ChannelType.Analog, 0, 2.0, T0.AddMilliseconds(1))));

        // The third sample closes the second row, which fills the budget: the sink asks to stop and
        // the sample that triggered it is not kept, so no half-row rides along behind the budget.
        Assert.False(sink.Add(Sample(ChannelType.Analog, 0, 3.0, T0.AddMilliseconds(2))));
        Assert.True(sink.IsComplete);

        var rows = sink.Complete();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new double?[] { 1.0 }, rows[0].Values);
        Assert.Equal(new double?[] { 2.0 }, rows[1].Values);
        Assert.Equal(2, sink.SampleCount);
    }

    // A capture bounded by time can stop part-way through a tick. That row is real data, one
    // channel short — dropping it would lose a sample the device did send.
    [Fact]
    public void Complete_FlushesTheRowStillBeingFilled()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0), Key(ChannelType.Analog, 1));

        sink.Add(Sample(ChannelType.Analog, 0, 1.0, T0));
        sink.Add(Sample(ChannelType.Analog, 1, 2.0, T0));
        sink.Add(Sample(ChannelType.Analog, 0, 3.0, T0.AddMilliseconds(1)));

        var rows = sink.Complete();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new double?[] { 3.0, null }, rows[1].Values);
    }

    [Fact]
    public void SampleForAColumnThatDoesNotExist_IsCountedNotFiled()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0));

        sink.Add(Sample(ChannelType.Digital, 0, 1, T0));

        Assert.Equal(1, sink.UnexpectedSampleCount);
        Assert.Empty(sink.Complete());
    }

    [Fact]
    public void DeviceTimestamp_IsCarriedOnTheRow()
    {
        var sink = NewSink(maxRows: 10, Key(ChannelType.Analog, 0));

        sink.Add(LiveSampleCaptureTests.Sample(ChannelType.Analog, 0, 1.0, T0, deviceTimestamp: 4242u));

        Assert.Equal(4242u, sink.Complete()[0].DeviceTimestamp);
    }

    [Theory]
    [InlineData(ChannelType.Analog, 3, "AI3")]
    [InlineData(ChannelType.Digital, 3, "DIO3")]
    public void ColumnLabels_NameTheChannelTheWayTheRestOfTheToolsDo(ChannelType type, int number, string expected)
    {
        Assert.Equal(expected, Key(type, number).Label);
    }

    private static SampleRowSink NewSink(int maxRows, params ChannelKey[] columns) => new(columns, maxRows);

    private static ChannelKey Key(ChannelType type, int number) => LiveSampleCaptureTests.Key(type, number);

    private static LiveSample Sample(ChannelType type, int number, double value, DateTime timestamp) =>
        LiveSampleCaptureTests.Sample(type, number, value, timestamp);
}
