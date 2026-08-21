using System.Diagnostics;
using Daqifi.Core.Channel;

namespace Daqifi.Mcp;

/// <summary>
/// Identifies a channel within a capture: type <b>and</b> number. The number alone is ambiguous —
/// a device numbers its analog inputs and its digital pins from 0 independently, so AI0 and DIO0
/// are different channels with the same number.
/// </summary>
internal readonly record struct ChannelKey(ChannelType Type, int Number)
{
    internal static ChannelKey For(IChannel channel) => new(channel.Type, channel.ChannelNumber);

    /// <summary>
    /// The column label an agent sees. <c>AI0</c>/<c>DIO0</c> rather than Core's "Analog Channel 0",
    /// matching the naming the SD-card CSV export already uses for analog columns.
    /// </summary>
    internal string Label => Type == ChannelType.Digital ? $"DIO{Number}" : $"AI{Number}";
}

/// <summary>
/// Where a capture puts the samples it reads. Two implementations, one per tool: the latest value
/// per channel (<see cref="LatestValueSink"/>) and timestamp-aligned rows (<see cref="SampleRowSink"/>).
/// </summary>
internal interface ILiveSampleSink
{
    /// <summary>Files one sample.</summary>
    /// <returns><c>false</c> when this sink has all it can hold and the capture should stop.</returns>
    bool Add(LiveSample sample);

    /// <summary>Whether the sink has everything it was asked for, so the capture can stop early.</summary>
    bool IsComplete { get; }

    /// <summary>
    /// Samples that arrived for a channel outside the set the capture was built for — a channel
    /// enabled by another caller after the capture started. Reported rather than silently dropped.
    /// </summary>
    long UnexpectedSampleCount { get; }
}

/// <summary>What a capture run did, as opposed to what it collected (which the sink holds).</summary>
/// <param name="SampleCount">Samples read off the live stream, including any the sink did not want.</param>
/// <param name="Elapsed">Wall-clock time the capture actually ran for.</param>
/// <param name="DataElapsed">
/// Wall-clock time between the first sample and the last — <see cref="Elapsed"/> without the wait
/// for the device to get going, which is 85-110 ms when the capture had to start the stream itself.
/// This is what a measured rate has to be divided by; dividing by <see cref="Elapsed"/> would
/// under-report the device by that wait, which on a short capture is most of it. Zero when fewer
/// than two samples arrived.
/// </param>
internal readonly record struct LiveCaptureOutcome(long SampleCount, TimeSpan Elapsed, TimeSpan DataElapsed);

/// <summary>
/// Drives the device's live sample stream into a sink for a bounded window — the shared engine
/// behind <c>read_channel_values</c> and <c>capture_samples</c> (#498).
/// </summary>
internal static class LiveSampleCapture
{
    /// <summary>
    /// Reads samples into <paramref name="sink"/> until it is complete, the window elapses, or the
    /// stream ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="onSubscribed"/> is where the caller starts the device's stream, and it runs
    /// where it does on purpose. Core's live stream subscribes to the channels' sample events in
    /// the part of its iterator body that runs <i>synchronously</i>, before the first await — so the
    /// subscription is in place once <c>MoveNextAsync</c> has returned, whether or not it has
    /// completed. Starting the device stream at that point rather than before the enumeration is
    /// what keeps the first samples of a capture from being decoded while nothing is listening.
    /// </para>
    /// <para>
    /// The window ending is not a failure: it is how a timed capture finishes, so the cancellation
    /// it raises is swallowed here. The caller's own <paramref name="cancellationToken"/> is not —
    /// that propagates, as does a <c>DeviceNotConnectedException</c> from an unplug mid-capture,
    /// because a capture cut short must not be indistinguishable from a complete one.
    /// </para>
    /// </remarks>
    /// <param name="samples">The device's live sample stream.</param>
    /// <param name="sink">Where samples are filed.</param>
    /// <param name="window">How long to read for.</param>
    /// <param name="onSubscribed">Run once the enumeration is subscribed; null when nothing is needed.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    internal static async Task<LiveCaptureOutcome> DrainAsync(
        IAsyncEnumerable<LiveSample> samples,
        ILiveSampleSink sink,
        TimeSpan window,
        Action? onSubscribed,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);

        long sampleCount = 0;
        TimeSpan firstSampleAt = TimeSpan.Zero;
        TimeSpan lastSampleAt = TimeSpan.Zero;
        await using var enumerator = samples.GetAsyncEnumerator(windowCts.Token);

        var pending = enumerator.MoveNextAsync();

        try
        {
            onSubscribed?.Invoke();
        }
        catch
        {
            // Starting the stream failed. The read already in flight has to be ended before the
            // enumerator can be disposed — disposing an async iterator with a MoveNextAsync
            // outstanding throws NotSupportedException, which would replace the failure the caller
            // actually needs to see with a meaningless one.
            windowCts.Cancel();
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // The read we just cancelled, or the same failure again. Either way the original
                // exception below is the one worth reporting.
            }

            throw;
        }

        try
        {
            while (await pending.ConfigureAwait(false))
            {
                lastSampleAt = stopwatch.Elapsed;
                if (sampleCount++ == 0)
                {
                    firstSampleAt = lastSampleAt;
                }

                if (!sink.Add(enumerator.Current) || sink.IsComplete)
                {
                    break;
                }

                pending = enumerator.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The capture window elapsed.
        }

        return new LiveCaptureOutcome(sampleCount, stopwatch.Elapsed, lastSampleAt - firstSampleAt);
    }
}

/// <summary>
/// Keeps the most recent sample per channel — what <c>read_channel_values</c> answers with. Completes
/// as soon as every expected channel has reported once, so a spot check on a healthy device returns
/// in about one sample period rather than waiting out its timeout.
/// </summary>
internal sealed class LatestValueSink : ILiveSampleSink
{
    private readonly Dictionary<ChannelKey, LiveSample> _latest = new();
    private readonly HashSet<ChannelKey> _expected;

    internal LatestValueSink(IEnumerable<ChannelKey> expected)
    {
        _expected = expected.ToHashSet();
    }

    public long UnexpectedSampleCount { get; private set; }

    public bool IsComplete => _latest.Count >= _expected.Count;

    public bool Add(LiveSample sample)
    {
        var key = ChannelKey.For(sample.Channel);
        if (!_expected.Contains(key))
        {
            UnexpectedSampleCount++;
            return true;
        }

        _latest[key] = sample;
        return true;
    }

    /// <summary>The latest sample for <paramref name="key"/>, or null when that channel never reported.</summary>
    internal IDataSample? Latest(ChannelKey key) => _latest.TryGetValue(key, out var sample) ? sample.Sample : null;

    /// <summary>How many of the expected channels have reported at least once.</summary>
    internal int ReportedChannelCount => _latest.Count;
}

/// <summary>
/// Groups samples into timestamp-aligned rows — what <c>capture_samples</c> returns. One row is one
/// device sample tick with a column per channel, the same shape as the SD-card CSV export.
/// </summary>
/// <remarks>
/// A row is closed when the timestamp changes <b>or</b> when a channel that already has a value in
/// the open row reports again. The second rule is what keeps a value from being overwritten on a
/// device whose clock hands out the same timestamp to consecutive frames — which firmware 3.7.2
/// does at high sample rates.
/// </remarks>
internal sealed class SampleRowSink : ILiveSampleSink
{
    private readonly IReadOnlyList<ChannelKey> _columns;
    private readonly Dictionary<ChannelKey, int> _columnIndex;
    private readonly int _maxRows;
    private readonly List<CaptureRow> _rows;

    private double?[]? _open;
    private DateTime _openTimestamp;
    private uint? _openDeviceTimestamp;

    internal SampleRowSink(IReadOnlyList<ChannelKey> columns, int maxRows)
    {
        _columns = columns;
        _columnIndex = new Dictionary<ChannelKey, int>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            _columnIndex[columns[i]] = i;
        }

        _maxRows = maxRows;
        _rows = new List<CaptureRow>(Math.Min(maxRows, 1024));
    }

    public long UnexpectedSampleCount { get; private set; }

    public bool IsComplete => _rows.Count >= _maxRows;

    /// <summary>
    /// Whether the row budget filled while the capture was <b>still running</b> — the honest answer
    /// to "was there more data?".
    /// </summary>
    /// <remarks>
    /// Latched here rather than left to be read off the row count afterwards, which is the same
    /// number for two different outcomes: <see cref="Complete"/> closes the row that was still being
    /// filled when the window ended, and that flush alone can bring the count up to the budget on a
    /// capture the budget had nothing to do with. A caller told the budget stopped it would go back
    /// for a continuation that does not exist.
    /// </remarks>
    internal bool RowBudgetFilled { get; private set; }

    /// <summary>Samples that landed in a row. Lower than the capture's sample count when channels were unexpected.</summary>
    internal long SampleCount { get; private set; }

    public bool Add(LiveSample sample)
    {
        if (IsComplete)
        {
            return false;
        }

        var key = ChannelKey.For(sample.Channel);
        if (!_columnIndex.TryGetValue(key, out var index))
        {
            UnexpectedSampleCount++;
            return true;
        }

        var timestamp = sample.Sample.Timestamp;
        if (_open is null)
        {
            OpenRow(timestamp, sample.Sample.DeviceTimestamp);
        }
        else if (timestamp != _openTimestamp || _open[index].HasValue)
        {
            CloseRow(duringCapture: true);
            if (IsComplete)
            {
                return false;
            }

            OpenRow(timestamp, sample.Sample.DeviceTimestamp);
        }

        _open![index] = sample.Sample.Value;
        SampleCount++;
        return true;
    }

    /// <summary>
    /// Closes the row still being filled when the capture ended. A capture bounded by time can stop
    /// part-way through a tick, and that row is real data — one channel short, not wrong.
    /// </summary>
    internal IReadOnlyList<CaptureRow> Complete()
    {
        if (_open is not null && !IsComplete)
        {
            CloseRow(duringCapture: false);
        }

        return _rows;
    }

    private void OpenRow(DateTime timestamp, uint? deviceTimestamp)
    {
        _open = new double?[_columns.Count];
        _openTimestamp = timestamp;
        _openDeviceTimestamp = deviceTimestamp;
    }

    /// <param name="duringCapture">
    /// False for the final flush from <see cref="Complete"/>, which is what keeps that row from
    /// being mistaken for the budget filling — see <see cref="RowBudgetFilled"/>.
    /// </param>
    private void CloseRow(bool duringCapture)
    {
        _rows.Add(new CaptureRow(_openTimestamp, _openDeviceTimestamp, _open!));
        _open = null;

        if (duringCapture && IsComplete)
        {
            RowBudgetFilled = true;
        }
    }
}
