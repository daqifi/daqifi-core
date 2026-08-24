using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// Covers the live-to-offline adapter on its own — no device involved, so every case here is
/// deterministic. The recorder that drives it against a device is covered by
/// <see cref="LiveCsvRecordingTests"/>.
/// </summary>
public class LiveSampleSourceTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Channel set and keys ────────────────────────────────────────────────

    [Fact]
    public void GetChannels_BuildsKeysFromDeviceIdentityAndChannelName()
    {
        var source = new LiveSampleSource(Empty(), [Analog(0, "AI0"), Digital(1, "DIO1")], "Nyquist", "SN-42");

        var channels = source.GetChannels();

        Assert.Equal(new[] { "Nyquist:SN-42:AI0", "Nyquist:SN-42:DIO1" }, channels.Select(c => c.Key).ToArray());
        Assert.Equal(ChannelType.Analog, channels[0].ChannelType);
        Assert.Equal(ChannelType.Digital, channels[1].ChannelType);
    }

    [Fact]
    public void GetChannels_PreservesTheOrderGiven_BecauseItIsTheColumnOrder()
    {
        var source = new LiveSampleSource(
            Empty(), [Analog(2, "AI2"), Analog(0, "AI0"), Analog(1, "AI1")], "Dev", "SN");

        Assert.Equal(
            new[] { "AI2", "AI0", "AI1" },
            source.GetChannels().Select(c => c.ChannelName).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankSerialNumber_BecomesUnknown_RatherThanAnEmptyKeySegment(string? serial)
    {
        var source = new LiveSampleSource(Empty(), [Analog(0, "AI0")], "Dev", serial);

        Assert.Equal("Dev:unknown:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void Constructor_BlankDeviceName_BecomesUnknown()
    {
        var source = new LiveSampleSource(Empty(), [Analog(0, "AI0")], "  ", "SN");

        Assert.Equal("unknown:SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void Constructor_DuplicateChannelIdentity_KeepsOneColumn()
    {
        // Two columns with the same key cannot be told apart in the output, and the exporter would
        // write the same value into both.
        var source = new LiveSampleSource(
            Empty(), [Analog(0, "AI0"), Analog(0, "AI0 again")], "Dev", "SN");

        var channel = Assert.Single(source.GetChannels());
        Assert.Equal("Dev:SN:AI0", channel.Key);
    }

    [Fact]
    public void Constructor_SameNumberDifferentType_AreDistinctChannels()
    {
        // A device numbers its analog inputs and its digital pins from 0 independently, so the
        // number alone is not an identity.
        var source = new LiveSampleSource(
            Empty(), [Analog(0, "AI0"), Digital(0, "DIO0")], "Dev", "SN");

        Assert.Equal(2, source.GetChannels().Count);
    }

    [Fact]
    public void Constructor_NullChannelEntry_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new LiveSampleSource(Empty(), [Analog(0, "AI0"), null!], "Dev", "SN"));

        Assert.Equal("channels", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Equal("samples", Assert.Throws<ArgumentNullException>(
            () => new LiveSampleSource(null!, [], "Dev", "SN")).ParamName);
        Assert.Equal("channels", Assert.Throws<ArgumentNullException>(
            () => new LiveSampleSource(Empty(), null!, "Dev", "SN")).ParamName);
        Assert.Equal("deviceName", Assert.Throws<ArgumentNullException>(
            () => new LiveSampleSource(Empty(), [], null!, "SN")).ParamName);
    }

    // ── Progress degradation ────────────────────────────────────────────────

    [Fact]
    public async Task GetSampleCountAsync_ReturnsZero_BecauseALiveStreamHasNoLength()
    {
        var source = new LiveSampleSource(Empty(), [Analog(0, "AI0")], "Dev", "SN");

        Assert.Equal(0, await source.GetSampleCountAsync());
    }

    [Fact]
    public async Task Export_WithProgress_ReportsOnlyCompletion_RatherThanDividingByAnUnknownTotal()
    {
        var ai0 = Analog(0, "AI0");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1.0), Sample(ai0, T0.AddSeconds(1), 2.0)),
            [ai0], "Dev", "SN");
        var reported = new List<int>();

        await ExportAsync(source, new CsvExportOptions(), new Progress<int>(p => { lock (reported) { reported.Add(p); } }));

        // Progress<T> posts asynchronously, so wait for the one report the exporter makes.
        await WaitForAsync(() => { lock (reported) { return reported.Count > 0; } });
        lock (reported)
        {
            Assert.All(reported, p => Assert.Equal(100, p));
        }
    }

    // ── Sample translation ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamSamples_EmitsOneRowPerSample_KeyedByChannel()
    {
        var ai0 = Analog(0, "AI0");
        var ai1 = Analog(1, "AI1");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1.5), Sample(ai1, T0, 2.5)),
            [ai0, ai1], "Dev", "SN");

        var rows = await ToListAsync(source.StreamSamples());

        Assert.Equal(
            new[] { (T0.Ticks, "Dev:SN:AI0", 1.5), (T0.Ticks, "Dev:SN:AI1", 2.5) },
            rows.Select(r => (r.TimestampTicks, r.ChannelKey, r.Value)).ToArray());
    }

    [Fact]
    public async Task StreamSamples_ExportsTheScaledValue_NotTheUnscaledOne()
    {
        // A user who configured a transducer conversion asked for PSI, not the volts underneath it.
        var ai0 = Analog(0, "AI0");
        var scaling = new ChannelScaling(10.0, 1.0, "PSI");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 2.0, scaling)), [ai0], "Dev", "SN");

        var row = Assert.Single(await ToListAsync(source.StreamSamples()));

        Assert.Equal(21.0, row.Value);
    }

    [Fact]
    public async Task StreamSamples_SampleForAChannelWithNoColumn_IsCountedAndLeftOut()
    {
        // A channel enabled by someone else after the recording started. Emitting it would produce
        // a row of empty cells under a key the header has no column for.
        var ai0 = Analog(0, "AI0");
        var ai7 = Analog(7, "AI7");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1.0), Sample(ai7, T0, 9.0)), [ai0], "Dev", "SN");

        var rows = await ToListAsync(source.StreamSamples());

        Assert.Equal("Dev:SN:AI0", Assert.Single(rows).ChannelKey);
        Assert.Equal(1L, source.UnmappedSampleCount);
        Assert.Equal(2L, source.SampleCount);
        Assert.Equal(1L, source.RowCount);
    }

    [Fact]
    public async Task StreamSamples_ChannelNumberMatchesButTypeDoesNot_IsUnmapped()
    {
        var analog0 = Analog(0, "AI0");
        var digital0 = Digital(0, "DIO0");
        var source = new LiveSampleSource(Stream(Sample(digital0, T0, 1.0)), [analog0], "Dev", "SN");

        Assert.Empty(await ToListAsync(source.StreamSamples()));
        Assert.Equal(1L, source.UnmappedSampleCount);
    }

    [Fact]
    public async Task StreamSamples_EmptyStream_LeavesEveryCounterAtZero()
    {
        var source = new LiveSampleSource(Empty(), [Analog(0, "AI0")], "Dev", "SN");

        Assert.Empty(await ToListAsync(source.StreamSamples()));
        Assert.Equal(0L, source.SampleCount);
        Assert.Equal(0L, source.RowCount);
        Assert.Equal(0L, source.UnmappedSampleCount);
        Assert.Equal(0L, source.NonMonotonicSampleCount);
    }

    // ── Counters ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RowCount_CountsTimestamps_NotSamples_AndMatchesTheLinesTheExporterWrites()
    {
        // The trap this counter exists for: every channel decoded from one device frame shares that
        // frame's timestamp, so counting samples would over-report by the channel count.
        var ai0 = Analog(0, "AI0");
        var ai1 = Analog(1, "AI1");
        var t1 = T0.AddSeconds(1);
        var t2 = T0.AddSeconds(2);
        var source = new LiveSampleSource(
            Stream(
                Sample(ai0, T0, 1), Sample(ai1, T0, 2),
                Sample(ai0, t1, 3), Sample(ai1, t1, 4),
                Sample(ai0, t2, 5), Sample(ai1, t2, 6)),
            [ai0, ai1], "Dev", "SN");

        var lines = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.Equal(6L, source.SampleCount);
        Assert.Equal(3L, source.RowCount);
        Assert.Equal(source.RowCount, (long)(lines.Length - 1)); // minus the header
    }

    [Fact]
    public async Task RowCount_ConsecutiveFramesRepeatingATimestamp_CountOneRow()
    {
        // The exporter collapses consecutive same-timestamp samples into one line even across
        // frames, so the counter has to follow the same rule to stay line-for-line accurate.
        var ai0 = Analog(0, "AI0");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1), Sample(ai0, T0, 2), Sample(ai0, T0.AddTicks(1), 3)),
            [ai0], "Dev", "SN");

        var lines = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.Equal(3L, source.SampleCount);
        Assert.Equal(2L, source.RowCount);
        Assert.Equal(source.RowCount, (long)(lines.Length - 1));
    }

    [Fact]
    public async Task NonMonotonicSampleCount_CountsBackwardsTimestamps_AndStillExportsThem()
    {
        var ai0 = Analog(0, "AI0");
        var source = new LiveSampleSource(
            Stream(
                Sample(ai0, T0.AddSeconds(2), 1),
                Sample(ai0, T0.AddSeconds(1), 2),
                Sample(ai0, T0.AddSeconds(3), 3)),
            [ai0], "Dev", "SN");

        var rows = await ToListAsync(source.StreamSamples());

        Assert.Equal(1L, source.NonMonotonicSampleCount);
        Assert.Equal(3, rows.Count); // losing data would be worse than reporting it out of order
        Assert.Equal(3L, source.RowCount);
    }

    [Fact]
    public async Task NonMonotonicSampleCount_AscendingStream_StaysZero()
    {
        var ai0 = Analog(0, "AI0");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1), Sample(ai0, T0.AddSeconds(1), 2), Sample(ai0, T0.AddSeconds(2), 3)),
            [ai0], "Dev", "SN");

        await ToListAsync(source.StreamSamples());

        Assert.Equal(0L, source.NonMonotonicSampleCount);
    }

    [Fact]
    public async Task NonMonotonicSampleCount_RepeatedTimestamp_IsNotBackwards()
    {
        var ai0 = Analog(0, "AI0");
        var ai1 = Analog(1, "AI1");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1), Sample(ai1, T0, 2)), [ai0, ai1], "Dev", "SN");

        await ToListAsync(source.StreamSamples());

        Assert.Equal(0L, source.NonMonotonicSampleCount);
    }

    [Fact]
    public async Task StreamSamples_UnmappedSampleDoesNotMoveTheTimestampCursor()
    {
        // An unmapped sample must not start a row of its own, nor make the next mapped sample at
        // the same timestamp look like a new one.
        var ai0 = Analog(0, "AI0");
        var ai7 = Analog(7, "AI7");
        var source = new LiveSampleSource(
            Stream(Sample(ai0, T0, 1), Sample(ai7, T0.AddSeconds(1), 2), Sample(ai0, T0, 3)),
            [ai0], "Dev", "SN");

        var lines = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.Equal(1L, source.RowCount);
        Assert.Equal(source.RowCount, (long)(lines.Length - 1));
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamSamples_Cancellation_EndsTheEnumeration()
    {
        // Proves the token reaches the wrapped live stream, not just the adapter's own loop.
        var ai0 = Analog(0, "AI0");
        using var cts = new CancellationTokenSource();
        var source = new LiveSampleSource(
            CancellableStream([Sample(ai0, T0, 1), Sample(ai0, T0.AddSeconds(1), 2)]), [ai0], "Dev", "SN");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.StreamSamples(cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1L, source.SampleCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IAnalogChannel Analog(int number, string name) =>
        new AnalogChannel(number) { Name = name };

    private static IDigitalChannel Digital(int number, string name) =>
        new DigitalChannel(number) { Name = name };

    private static LiveSample Sample(IChannel channel, DateTime timestamp, double value, ChannelScaling? scaling = null) =>
        new(channel, new DataSample { Timestamp = timestamp, Value = value, Scaling = scaling });

    private static async IAsyncEnumerable<LiveSample> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<LiveSample> CancellableStream(
        LiveSample[] samples,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return sample;
        }
    }

    private static async IAsyncEnumerable<LiveSample> Stream(params LiveSample[] samples)
    {
        foreach (var sample in samples)
        {
            await Task.Yield();
            yield return sample;
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private static async Task<string> ExportAsync(
        ISampleSource source, CsvExportOptions options, IProgress<int>? progress = null)
    {
        var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, options, progress);
        return writer.ToString();
    }

    private static async Task<string[]> ExportToLinesAsync(ISampleSource source, CsvExportOptions options) =>
        (await ExportAsync(source, options))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not satisfied within the timeout.");
    }
}
