using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Adapts a live device stream — an <see cref="IAsyncEnumerable{T}"/> of <see cref="LiveSample"/>,
/// as produced by <see cref="ILiveSampleSource.StreamSamplesAsync"/> — to the offline
/// <see cref="ISampleSource"/> that <see cref="CsvExporter"/> consumes, so samples can be written
/// to CSV as they arrive rather than buffered into a session first.
/// </summary>
/// <remarks>
/// <para>
/// This is the join between the two sample abstractions the package ships. Before it,
/// <see cref="ILiveSampleSource"/> handed a caller an <c>IAsyncEnumerable&lt;LiveSample&gt;</c> in
/// one namespace and <see cref="CsvExporter"/> asked for an <see cref="ISampleSource"/> in another,
/// with no supported way to connect them — so the most basic DAQ workflow, stream from the device
/// and record it to a file, had no answer in Core at all (issue #639).
/// </para>
/// <para>
/// <b>Nothing is buffered.</b> Each <see cref="LiveSample"/> is translated into a
/// <see cref="SampleRow"/> and yielded straight through, so a recording's memory does not grow with
/// its length. The counters below are the only state kept, and they are all scalars.
/// </para>
/// <para>
/// <b>The channel set is fixed when this source is constructed</b>, because
/// <see cref="ISampleSource.GetChannels"/> is synchronous and the exporter calls it before it reads
/// a single sample — a CSV cannot grow a column halfway down the file. A sample that arrives for a
/// channel outside that set (one enabled by another caller after the recording started) is counted
/// in <see cref="UnmappedSampleCount"/> and dropped, rather than silently emitted under a key with
/// no column, which would have produced a row of empty cells.
/// </para>
/// <para>
/// Samples are matched to channels by <see cref="IChannel.Type"/> and
/// <see cref="IChannel.ChannelNumber"/> rather than by object identity, because a device rebuilds
/// its channel objects when it repopulates them from a status message. The number alone is
/// ambiguous — a device numbers its analog inputs and its digital pins from 0 independently — which
/// is why the type is part of the key.
/// </para>
/// </remarks>
public sealed class LiveSampleSource : ISampleSource
{
    /// <summary>
    /// Stands in for a device name or serial number that is missing or blank. A blank string is a
    /// gap rather than an answer, and letting one through would produce channel keys like
    /// <c>Daqifi::AI0</c> whose columns cannot be told apart between two unidentified devices.
    /// </summary>
    private const string Unknown = "unknown";

    private readonly IAsyncEnumerable<LiveSample> _samples;
    private readonly List<ChannelDescriptor> _channels;
    private readonly Dictionary<(ChannelType Type, int Number), string> _keysByChannel;

    /// <summary>
    /// Creates a source that replays <paramref name="samples"/> as export rows for the given channels.
    /// </summary>
    /// <param name="samples">
    /// The live stream to read, normally <see cref="ILiveSampleSource.StreamSamplesAsync"/>. It is
    /// enumerated once, when the exporter calls <see cref="StreamSamples"/>.
    /// </param>
    /// <param name="channels">
    /// The channels to give columns to, in the order the columns should appear. Normally the
    /// enabled subset of <see cref="IStreamingDevice.GetChannelsSnapshot"/>. Duplicates — entries
    /// sharing a <see cref="IChannel.Type"/> and <see cref="IChannel.ChannelNumber"/> — are ignored
    /// after the first, since two identical columns cannot be told apart in the output.
    /// </param>
    /// <param name="deviceName">The device name to build channel keys from.</param>
    /// <param name="deviceSerialNumber">
    /// The device serial number to build channel keys from. Null or blank becomes
    /// <c>"unknown"</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="samples"/>, <paramref name="channels"/> or <paramref name="deviceName"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="channels"/> contains a null entry.</exception>
    public LiveSampleSource(
        IAsyncEnumerable<LiveSample> samples,
        IEnumerable<IChannel> channels,
        string deviceName,
        string? deviceSerialNumber)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(deviceName);

        _samples = samples;

        var name = string.IsNullOrWhiteSpace(deviceName) ? Unknown : deviceName;
        var serial = string.IsNullOrWhiteSpace(deviceSerialNumber) ? Unknown : deviceSerialNumber;

        _channels = [];
        _keysByChannel = [];

        foreach (var channel in channels)
        {
            if (channel is null)
            {
                throw new ArgumentException("The channel collection contains a null entry.", nameof(channels));
            }

            var identity = (channel.Type, channel.ChannelNumber);
            if (_keysByChannel.ContainsKey(identity))
            {
                continue;
            }

            var descriptor = new ChannelDescriptor(name, serial, channel.Name, channel.Type);
            _channels.Add(descriptor);
            _keysByChannel[identity] = descriptor.Key;
        }
    }

    /// <summary>
    /// Gets the number of live samples read off the stream, including any counted in
    /// <see cref="UnmappedSampleCount"/>. This is samples, not CSV lines — see <see cref="RowCount"/>.
    /// </summary>
    public long SampleCount { get; private set; }

    /// <summary>
    /// Gets the number of distinct consecutive timestamps seen, which is the number of CSV data
    /// lines a default export writes.
    /// </summary>
    /// <remarks>
    /// A row is a timestamp, not a sample: <see cref="CsvExporter"/> collapses every consecutive
    /// <see cref="SampleRow"/> sharing a timestamp into one line, and every channel decoded from one
    /// device stream frame shares that frame's timestamp exactly. Reporting
    /// <see cref="SampleCount"/> as the line count would therefore over-report by the channel count.
    /// This tracks the exporter's own flush rule, so it holds even when consecutive frames repeat a
    /// timestamp. It does <b>not</b> describe an averaged export
    /// (<see cref="CsvExportOptions.AverageWindow"/>), which writes one line per window instead.
    /// </remarks>
    public long RowCount { get; private set; }

    /// <summary>
    /// Gets the number of samples that arrived for a channel this source has no column for, and
    /// were therefore left out of the export. Non-zero means the recording is missing data the
    /// device sent, so it is reported rather than swallowed.
    /// </summary>
    public long UnmappedSampleCount { get; private set; }

    /// <summary>
    /// Gets the number of samples whose timestamp went backwards relative to the sample before it.
    /// </summary>
    /// <remarks>
    /// <see cref="ISampleSource.StreamSamples"/> is contractually ascending, and the live path is
    /// expected to satisfy it: <see cref="TimestampProcessor"/> reconstructs a monotonic host
    /// timestamp across the device counter's ~86-second rollover, and the live buffer preserves
    /// arrival order. This counts the exception rather than assuming it away — a device clock reset,
    /// a rollover mis-detection, or a caller feeding this source a stream of its own can all break
    /// the assumption, and the resulting CSV has a time column that goes backwards. Rows are still
    /// exported: losing data is worse than exporting it out of order, and a caller that cannot
    /// tolerate it can see that it happened.
    /// </remarks>
    public long NonMonotonicSampleCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ChannelDescriptor> GetChannels() => _channels;

    /// <summary>
    /// Always returns 0 — a live stream has no length until it ends.
    /// </summary>
    /// <remarks>
    /// This is exactly the "count is unavailable" case <see cref="ISampleSource.GetSampleCountAsync"/>
    /// documents, and returning 0 is what makes <see cref="CsvExporter"/> skip percentage progress
    /// rather than divide by a total it does not have. Progress over a live recording is a sample
    /// count, not a percentage; read <see cref="SampleCount"/> or <see cref="RowCount"/> for that.
    /// </remarks>
    /// <param name="cancellationToken">Unused; the answer needs no work.</param>
    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    /// <summary>
    /// Streams the live samples as export rows, one row per channel value, updating the counters as
    /// it goes.
    /// </summary>
    /// <remarks>
    /// The value written is <see cref="IDataSample.ScaledValue"/> — the sample in the engineering
    /// units the channel was configured with, which is the plain reading when the channel has no
    /// scaling. That is the number the caller asked the device for; exporting the pre-scaling
    /// <see cref="IDataSample.Value"/> would silently discard a conversion the user set up.
    /// </remarks>
    /// <param name="cancellationToken">Ends the enumeration, and with it the underlying live stream.</param>
    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long? previousTicks = null;

        await foreach (var live in _samples.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            SampleCount++;

            if (live?.Channel is not { } channel
                || !_keysByChannel.TryGetValue((channel.Type, channel.ChannelNumber), out var key))
            {
                UnmappedSampleCount++;
                continue;
            }

            var ticks = live.Sample.Timestamp.Ticks;

            // Mirrors the exporter's own flush rule — a new line whenever the timestamp changes —
            // so the count matches the file line for line.
            if (previousTicks != ticks)
            {
                RowCount++;
            }

            if (previousTicks is { } previous && ticks < previous)
            {
                NonMonotonicSampleCount++;
            }

            previousTicks = ticks;

            yield return new SampleRow(ticks, key, live.Sample.ScaledValue);
        }
    }
}
