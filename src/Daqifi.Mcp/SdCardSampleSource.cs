using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Mcp;

/// <summary>
/// Adapts a parsed <see cref="SdCardLogSession"/> to <see cref="ISampleSource"/> so Core's
/// <see cref="CsvExporter"/> can turn a downloaded SD-card log into a CSV the agent can read.
/// Emits one column per analog channel plus one for the digital port (the raw port value).
/// </summary>
/// <remarks>
/// Also counts what the export produced, because the agent is told both numbers and they are not
/// the same thing: <see cref="SampleCount"/> is log entries read, <see cref="RowCount"/> is CSV
/// lines written. The exporter collapses every consecutive <see cref="SampleRow"/> sharing a
/// timestamp into one line, so a row is a timestamp, not a sample — counting samples would
/// over-report by the channel count.
/// </remarks>
internal sealed class SdCardSampleSource : ISampleSource
{
    private const string DeviceName = "Daqifi";
    private const string DigitalChannelName = "DIO";

    private readonly IAsyncEnumerable<SdCardLogEntry> _samples;
    private readonly List<ChannelDescriptor> _channels;
    private readonly string[] _analogKeys;
    private readonly string _digitalKey;

    public SdCardSampleSource(
        IAsyncEnumerable<SdCardLogEntry> samples, string? deviceSerialNumber, int analogPortCount)
    {
        _samples = samples;
        var serial = string.IsNullOrWhiteSpace(deviceSerialNumber) ? "unknown" : deviceSerialNumber;
        var analogCount = Math.Max(0, analogPortCount);

        _channels = new List<ChannelDescriptor>(analogCount + 1);
        _analogKeys = new string[analogCount];
        for (var i = 0; i < analogCount; i++)
        {
            var descriptor = new ChannelDescriptor(DeviceName, serial, $"AI{i}", ChannelType.Analog);
            _channels.Add(descriptor);
            _analogKeys[i] = descriptor.Key;
        }

        var digital = new ChannelDescriptor(DeviceName, serial, DigitalChannelName, ChannelType.Digital);
        _channels.Add(digital);
        _digitalKey = digital.Key;
    }

    /// <summary>CSV lines the export produced — one per distinct consecutive timestamp.</summary>
    public long RowCount { get; private set; }

    /// <summary>Log entries read from the file.</summary>
    public long SampleCount { get; private set; }

    /// <summary>
    /// Analog values seen on a single entry that had nowhere to go, because the entry carried more
    /// analog values than the channel count this source was built with. Non-zero means the CSV is
    /// missing columns, so the tool reports it instead of quietly truncating.
    /// </summary>
    public int DroppedAnalogColumns { get; private set; }

    public IReadOnlyList<ChannelDescriptor> GetChannels() => _channels;

    // 0 means "unknown", which tells CsvExporter to skip percentage progress. Knowing the real
    // count would mean reading the whole file first, which is the opposite of streaming it.
    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long? currentTicks = null;

        await foreach (var entry in _samples.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            SampleCount++;

            var ticks = entry.Timestamp.Ticks;

            // Mirrors the exporter's own flush rule (a new line whenever the timestamp changes), so
            // the count matches the file line for line — including when consecutive entries repeat
            // a timestamp and the exporter merges them into one row.
            if (currentTicks != ticks)
            {
                currentTicks = ticks;
                RowCount++;
            }

            var overflow = entry.AnalogValues.Count - _analogKeys.Length;
            if (overflow > DroppedAnalogColumns)
            {
                DroppedAnalogColumns = overflow;
            }

            var count = Math.Min(entry.AnalogValues.Count, _analogKeys.Length);
            for (var i = 0; i < count; i++)
            {
                yield return new SampleRow(ticks, _analogKeys[i], entry.AnalogValues[i]);
            }

            yield return new SampleRow(ticks, _digitalKey, entry.DigitalData);
        }
    }
}
