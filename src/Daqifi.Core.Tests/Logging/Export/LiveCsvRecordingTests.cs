using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Logging.Export;
using Daqifi.Core.Tests.Firmware;
using ChannelFactory = System.Threading.Channels.Channel;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// Covers <see cref="LiveCsvRecordingExtensions.RecordLiveSamplesToCsvAsync"/> — the join between a
/// device's live stream and <see cref="CsvExporter"/> that issue #639 was filed for.
/// </summary>
/// <remarks>
/// Most cases drive a device whose live stream is scripted (<see cref="ScriptedLiveDevice"/>), so
/// what arrives and when is exact rather than timed. The last test drives the real decode path of a
/// real <see cref="DaqifiStreamingDevice"/> instead, because the argument for this design is that it
/// records an actual live stream — a scripted one cannot show that the timestamps a frame produces
/// collapse into one CSV line, or that they come out ascending.
/// </remarks>
public class LiveCsvRecordingTests
{
    /// <summary>
    /// Upper bound on every await here. A regression in the stop path is "the recording never
    /// returns", which would otherwise park the run rather than fail it.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_NullDevice_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ((IStreamingDevice)null!).RecordLiveSamplesToCsvAsync(new StringWriter()));

        Assert.Equal("device", ex.ParamName);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_NullWriter_Throws()
    {
        using var device = CreateScripted();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => device.RecordLiveSamplesToCsvAsync(null!));

        Assert.Equal("writer", ex.ParamName);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_DeviceThatCannotSupplyLiveSamples_Throws()
    {
        // A streaming device is not automatically a live-sample source; the recording has nothing
        // to read and should say so at the call rather than write an empty file.
        var device = new FakeStreamingDevice("no-live-samples");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => device.RecordLiveSamplesToCsvAsync(new StringWriter()));

        Assert.Equal("device", ex.ParamName);
        Assert.Contains(nameof(ILiveSampleSource), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecordLiveSamplesToCsvAsync_NonPositiveDuration_Throws(int seconds)
    {
        using var device = CreateScripted();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.RecordLiveSamplesToCsvAsync(
                new StringWriter(), duration: TimeSpan.FromSeconds(seconds)));

        Assert.Equal("duration", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecordLiveSamplesToCsvAsync_BufferCapacityBelowOne_ThrowsAtTheCall(int capacity)
    {
        // The live stream defers this check to the first MoveNextAsync, which happens inside the
        // exporter where the argument is no longer the caller's to fix.
        using var device = CreateScripted();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.RecordLiveSamplesToCsvAsync(new StringWriter(), bufferCapacity: capacity));

        Assert.Equal("bufferCapacity", ex.ParamName);
        Assert.False(device.StreamRequested);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_AlreadyCancelledToken_ThrowsWithoutTouchingTheStream()
    {
        using var device = CreateScripted();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.RecordLiveSamplesToCsvAsync(new StringWriter(), cancellationToken: cts.Token));

        Assert.False(device.StreamRequested);
    }

    // ── Channel selection ───────────────────────────────────────────────────

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_NoEnabledChannels_WritesNothingAndNeverStartsTheStream()
    {
        // There is nothing to record, and a header of no columns is not a useful file.
        using var device = CreateScripted(analogCount: 2);
        var writer = new StringWriter();

        var result = await device.RecordLiveSamplesToCsvAsync(writer).WaitAsync(Timeout);

        Assert.Equal(string.Empty, writer.ToString());
        Assert.Equal(default(LiveCsvRecordingResult), result);
        Assert.False(device.StreamRequested);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_GivesColumnsToEnabledChannelsOnly()
    {
        using var device = CreateScripted(analogCount: 3);
        var channels = device.GetChannelsSnapshot();
        channels[0].IsEnabled = true;
        channels[2].IsEnabled = true;
        var writer = new StringWriter();

        var recording = device.RecordLiveSamplesToCsvAsync(writer, duration: TimeSpan.FromMilliseconds(200));
        device.Emit(Sample(channels[0], T0, 1.0));
        device.Emit(Sample(channels[2], T0, 3.0));
        await recording.WaitAsync(Timeout);

        var header = Lines(writer)[0];
        Assert.Equal(
            $"Time,{Key(device, channels[0])},{Key(device, channels[2])}",
            header);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_SampleForADisabledChannel_IsReportedNotExported()
    {
        using var device = CreateScripted(analogCount: 2);
        var channels = device.GetChannelsSnapshot();
        channels[0].IsEnabled = true;
        var writer = new StringWriter();

        var recording = device.RecordLiveSamplesToCsvAsync(writer, duration: TimeSpan.FromMilliseconds(200));
        device.Emit(Sample(channels[0], T0, 1.0));
        device.Emit(Sample(channels[1], T0, 2.0));
        var result = await recording.WaitAsync(Timeout);

        Assert.Equal(1L, result.UnmappedSampleCount);
        Assert.Equal(2L, result.SampleCount);
        Assert.Equal(1L, result.RowCount);
        Assert.Equal(2, Lines(writer).Length); // header + one data line
    }

    // ── Stopping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_DurationElapsing_FinishesTheFileRatherThanAborting()
    {
        // The window ending is how a timed recording finishes, so the frame the exporter was
        // accumulating when the window closed still has to reach the file. Letting the window's
        // cancellation tear through the exporter instead would silently drop that last line.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        var writer = new StringWriter();

        var recording = device.RecordLiveSamplesToCsvAsync(writer, duration: TimeSpan.FromMilliseconds(300));
        device.Emit(Sample(channel, T0, 1.0));
        device.Emit(Sample(channel, T0.AddSeconds(1), 2.0));
        device.Emit(Sample(channel, T0.AddSeconds(2), 3.0));
        var result = await recording.WaitAsync(Timeout);

        var lines = Lines(writer);
        Assert.Equal(4, lines.Length); // header + three data lines, the last one included
        Assert.EndsWith(",3", lines[3]);
        Assert.Equal(3L, result.RowCount);
        Assert.Equal(3L, result.SampleCount);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_StreamEnding_ReturnsTheResult()
    {
        // What a disconnect looks like from here: the enumeration ends, and so does the recording.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        var writer = new StringWriter();

        var recording = device.RecordLiveSamplesToCsvAsync(writer);
        device.Emit(Sample(channel, T0, 1.0));
        device.EndStream();
        var result = await recording.WaitAsync(Timeout);

        Assert.Equal(1L, result.RowCount);
        Assert.Equal(2, Lines(writer).Length);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_CallerCancellation_Propagates()
    {
        // A recording the caller aborted must not be indistinguishable from one that finished.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        using var cts = new CancellationTokenSource();

        var recording = device.RecordLiveSamplesToCsvAsync(new StringWriter(), cancellationToken: cts.Token);
        device.Emit(Sample(channel, T0, 1.0));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_CallerCancellationDuringADurationWindow_StillPropagates()
    {
        // The two stops are not interchangeable: a window is in play here, and the abort must still
        // win rather than being absorbed as a clean finish.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        using var cts = new CancellationTokenSource();

        var recording = device.RecordLiveSamplesToCsvAsync(
            new StringWriter(), duration: TimeSpan.FromSeconds(30), cancellationToken: cts.Token);
        device.Emit(Sample(channel, T0, 1.0));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_CallerCancellationRacingTheWindow_IsStillAnAbort()
    {
        // The narrow case the window's absorb rule has to get right: by the time the cancellation
        // surfaces, BOTH the window and the caller have cancelled. Absorbing it because the window
        // fired would turn an abort the caller asked for into a recording that looks complete.
        using var device = CreateScripted(analogCount: 1);
        device.GetChannelsSnapshot()[0].IsEnabled = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.GateStreamOn(gate);
        using var cts = new CancellationTokenSource();

        var recording = device.RecordLiveSamplesToCsvAsync(
            new StringWriter(), duration: TimeSpan.FromMilliseconds(50), cancellationToken: cts.Token);

        // Let the window elapse first, so the abort lands on top of an already-cancelled window.
        await Task.Delay(300);
        await cts.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitAsync(Timeout));
    }

    // ── Result ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_DroppedSamples_AreCountedAcrossTheRecordingOnly()
    {
        // The device's counter is cumulative across every enumeration it has ever served, so the
        // recording's own losses are the difference across it — reporting the raw value would blame
        // this recording for drops that happened before it started.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        device.Dropped = 7;
        device.OnStreamRequested = () => device.Dropped = 10;

        var recording = device.RecordLiveSamplesToCsvAsync(
            new StringWriter(), duration: TimeSpan.FromMilliseconds(200));
        device.Emit(Sample(channel, T0, 1.0));
        var result = await recording.WaitAsync(Timeout);

        Assert.Equal(3L, result.DroppedSampleCount);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_BufferCapacity_ReachesTheLiveStream()
    {
        using var device = CreateScripted(analogCount: 1);
        device.GetChannelsSnapshot()[0].IsEnabled = true;

        var recording = device.RecordLiveSamplesToCsvAsync(
            new StringWriter(), duration: TimeSpan.FromMilliseconds(200), bufferCapacity: 8);
        await recording.WaitAsync(Timeout);

        Assert.Equal(8, device.RequestedBufferCapacity);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_Options_ReachTheExporter()
    {
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        var writer = new StringWriter();

        var recording = device.RecordLiveSamplesToCsvAsync(
            writer,
            new CsvExportOptions { UseRelativeTime = true, Delimiter = ";" },
            duration: TimeSpan.FromMilliseconds(200));
        device.Emit(Sample(channel, T0, 1.0));
        device.Emit(Sample(channel, T0.AddSeconds(2), 2.0));
        await recording.WaitAsync(Timeout);

        var lines = Lines(writer);
        Assert.StartsWith("Relative Time (s);", lines[0]);
        Assert.StartsWith("0.000;", lines[1]);
        Assert.StartsWith("2.000;", lines[2]);
    }

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_FlushesTheWriterButDoesNotDisposeIt()
    {
        // A result that reports rows while the rows sit in an unflushed buffer is a foot-gun, and
        // the writer belongs to the caller, who may still have more to write to it.
        using var device = CreateScripted(analogCount: 1);
        var channel = device.GetChannelsSnapshot()[0];
        channel.IsEnabled = true;
        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

        var recording = device.RecordLiveSamplesToCsvAsync(writer, duration: TimeSpan.FromMilliseconds(200));
        device.Emit(Sample(channel, T0, 1.0));
        var result = await recording.WaitAsync(Timeout);

        Assert.Equal(1L, result.RowCount);
        Assert.NotEqual(0, stream.Length);
        await writer.WriteLineAsync("still usable"); // would throw on a disposed writer
        await writer.FlushAsync();
    }

    // ── The real decode path ────────────────────────────────────────────────

    [Fact]
    public async Task RecordLiveSamplesToCsvAsync_AgainstARealDeviceStream_CollapsesEachFrameIntoOneAscendingRow()
    {
        // The claim this whole design rests on: a real live stream, recorded as it arrives, comes out
        // as one CSV line per device frame, in time order, with nothing lost. A scripted stream
        // cannot show that — the frame-to-timestamp relationship is the decoder's, not the adapter's.
        using var device = new LiveStreamDevice("RealPath");
        device.Connect();
        device.Metadata.SerialNumber = "SN-REAL";
        device.PopulateChannelsFromStatus(StatusWithAnalogChannels(2));
        var channels = device.GetChannelsSnapshot();
        foreach (var channel in channels)
        {
            channel.IsEnabled = true;
        }

        device.StartStreaming();
        var writer = new StringWriter();
        var recording = device.RecordLiveSamplesToCsvAsync(writer, duration: TimeSpan.FromSeconds(2));

        // The recording subscribes from inside the exporter, so there is no synchronous point for a
        // test to hook. Give it time to get there before injecting, or the leading frames decode
        // while nothing is listening.
        await Task.Delay(400);
        for (uint i = 0; i < 20; i++)
        {
            device.InvokeStreamMessage(AnalogFrame(1000 + i * 500000, i, i + 100));
        }

        var result = await recording.WaitAsync(Timeout);

        var lines = Lines(writer);
        Assert.Equal($"Time,{Key(device, channels[0])},{Key(device, channels[1])}", lines[0]);
        Assert.Equal(20L, result.RowCount);
        Assert.Equal(40L, result.SampleCount); // two channels per frame, one line per frame
        Assert.Equal(lines.Length - 1, (int)result.RowCount);
        Assert.Equal(0L, result.DroppedSampleCount);
        Assert.Equal(0L, result.UnmappedSampleCount);

        // The exporter's contract is an ascending stream, and this is the live path honouring it
        // across the decoder's rollover-aware timestamp reconstruction.
        Assert.Equal(0L, result.NonMonotonicSampleCount);
        var timestamps = lines.Skip(1).Select(l => DateTime.Parse(l.Split(',')[0], CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)).ToArray();
        Assert.Equal(timestamps.OrderBy(t => t).ToArray(), timestamps);
        Assert.All(lines.Skip(1), l => Assert.Equal(3, l.Split(',').Length));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ScriptedLiveDevice CreateScripted(int analogCount = 1)
    {
        var device = new ScriptedLiveDevice("Scripted");
        device.Connect();
        device.Metadata.SerialNumber = "SN-1";
        device.PopulateChannelsFromStatus(StatusWithAnalogChannels(analogCount));
        return device;
    }

    private static DaqifiOutMessage StatusWithAnalogChannels(int analogCount)
    {
        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = (uint)analogCount,
            DigitalPortNum = 0,
            AnalogInRes = 65535,
        };
        for (var i = 0; i < analogCount; i++)
        {
            status.AnalogInPortRange.Add(1.0f);
        }

        return status;
    }

    private static DaqifiOutMessage AnalogFrame(uint timestamp, params float[] values)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        foreach (var value in values)
        {
            frame.AnalogInDataFloat.Add(value);
        }

        return frame;
    }

    private static LiveSample Sample(IChannel channel, DateTime timestamp, double value) =>
        new(channel, new DataSample { Timestamp = timestamp, Value = value });

    private static string Key(IStreamingDevice device, IChannel channel) =>
        $"{device.Name}:{device.Metadata.SerialNumber}:{channel.Name}";

    private static string[] Lines(StringWriter writer) =>
        writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

    /// <summary>
    /// A real streaming device whose live stream is replaced by one the test writes into, so what
    /// arrives and when is exact. The rest of the device — its channels, its metadata, its
    /// identity — is the real thing, because that is what the recording reads to build its columns.
    /// </summary>
    private sealed class ScriptedLiveDevice : DaqifiStreamingDevice, ILiveSampleSource
    {
        private readonly System.Threading.Channels.Channel<LiveSample> _samples =
            ChannelFactory.CreateUnbounded<LiveSample>();

        public ScriptedLiveDevice(string name) : base(name) { }

        public long Dropped { get; set; }

        public bool StreamRequested { get; private set; }

        public int? RequestedBufferCapacity { get; private set; }

        /// <summary>Runs when the recording asks for the stream — after it has read the drop counter.</summary>
        public Action? OnStreamRequested { get; set; }

        private TaskCompletionSource? _gate;

        public void Emit(LiveSample sample) => _samples.Writer.TryWrite(sample);

        /// <summary>
        /// Makes the stream park until <paramref name="gate"/> is completed and then surface the
        /// cancellation state as of that moment, so a test can decide exactly which tokens are
        /// cancelled when the enumeration throws.
        /// </summary>
        public void GateStreamOn(TaskCompletionSource gate) => _gate = gate;

        public void EndStream() => _samples.Writer.TryComplete();

        public override void Send<T>(IOutboundMessage<T> message) { /* no transport in tests */ }

        long ILiveSampleSource.DroppedLiveSampleCount => Dropped;

        IAsyncEnumerable<LiveSample> ILiveSampleSource.StreamSamplesAsync(
            CancellationToken cancellationToken, int? bufferCapacity)
        {
            StreamRequested = true;
            RequestedBufferCapacity = bufferCapacity;
            OnStreamRequested?.Invoke();
            return _gate is { } gate
                ? GatedAsync(gate, cancellationToken)
                : _samples.Reader.ReadAllAsync(cancellationToken);
        }

        private static async IAsyncEnumerable<LiveSample> GatedAsync(
            TaskCompletionSource gate,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await gate.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class LiveStreamDevice : DaqifiStreamingDevice
    {
        public LiveStreamDevice(string name) : base(name) { }

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message) { /* no transport in tests */ }
    }
}
