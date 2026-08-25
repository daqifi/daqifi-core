using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Adapts a parsed SD-card log — the <see cref="SdCardLogEntry"/> stream behind
/// <see cref="SdCardLogSession.Samples"/> — to the <see cref="ISampleSource"/> that
/// <see cref="CsvExporter"/> consumes, so a downloaded log can be turned into a CSV without the
/// caller writing the adapter itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the SD-card counterpart of <see cref="LiveSampleSource"/>, and the other half of the
/// join described there: Core shipped the parsers that produce an <see cref="SdCardLogSession"/>
/// and the exporter that consumes an <see cref="ISampleSource"/>, but nothing in between — so
/// every consumer that wanted a CSV out of a log file wrote the same adapter (issue #655).
/// </para>
/// <para>
/// <b>The layout is one column per analog channel plus one for the digital port.</b> Analog
/// columns are named <c>AI0</c>…<c>AI(n-1)</c> and carry the entry's
/// <see cref="SdCardLogEntry.AnalogValues"/> positionally; the digital column is named
/// <see cref="DigitalChannelName"/> and carries <see cref="SdCardLogEntry.DigitalData"/> as its
/// raw port value. A log records the port, not the individual pins, so splitting it into per-pin
/// columns here would be an invention rather than a decode.
/// </para>
/// <para>
/// <b>Nothing is buffered.</b> Entries are translated to <see cref="SampleRow"/> values and
/// yielded straight through, so exporting a large log costs one read buffer rather than a session
/// in memory. The counters below are the only state kept, and they are all scalars.
/// </para>
/// <para>
/// <b>Enumerate one export at a time.</b> <see cref="StreamSamples"/> reads the entry stream this
/// source was built with, and a session parsed from a caller-supplied <see cref="System.IO.Stream"/>
/// has a single read cursor: a second, overlapping enumeration throws rather than interleaving
/// reads and handing back corrupt samples. Two concurrent exports of one stream-backed session are
/// therefore not supported — export sequentially, or parse the log twice. A session parsed from a
/// file path re-opens the file for each enumeration and has no such limit.
/// </para>
/// </remarks>
public sealed class SdCardLogSampleSource : ISampleSource
{
    /// <summary>
    /// The device name used in channel keys when the caller supplies none. An SD-card log records
    /// a serial number and a part number but no device name, so there is nothing better to read
    /// off the file; this is the name every hand-rolled copy of this adapter used before it moved
    /// into Core, so keys stay comparable with CSVs those produced.
    /// </summary>
    public const string DefaultDeviceName = "Daqifi";

    /// <summary>
    /// The channel name given to the digital-port column.
    /// </summary>
    public const string DigitalChannelName = "DIO";

    /// <summary>
    /// Stands in for a serial number that is missing or blank. A blank string is a gap rather than
    /// an answer, and letting one through produces channel keys like <c>Daqifi::AI0</c> whose
    /// columns cannot be told apart between two unidentified devices — the same disposition
    /// <see cref="LiveSampleSource"/> and #627 take.
    /// </summary>
    private const string Unknown = "unknown";

    private readonly IAsyncEnumerable<SdCardLogEntry> _samples;
    private readonly List<ChannelDescriptor> _channels;
    private readonly string[] _analogKeys;
    private readonly string _digitalKey;

    /// <summary>
    /// Creates a source over a parsed log's entry stream.
    /// </summary>
    /// <param name="samples">
    /// The entries to read, normally <see cref="SdCardLogSession.Samples"/>. It is enumerated when
    /// the exporter calls <see cref="StreamSamples"/>, not before.
    /// </param>
    /// <param name="deviceSerialNumber">
    /// The serial number to build channel keys from, normally
    /// <see cref="SdCardDeviceConfiguration.DeviceSerialNumber"/>. Null or blank becomes
    /// <c>"unknown"</c>.
    /// </param>
    /// <param name="analogChannelCount">
    /// How many analog columns to emit, normally
    /// <see cref="SdCardDeviceConfiguration.AnalogPortCount"/>. Zero is allowed and exports the
    /// digital column alone — <see cref="CsvExporter"/> writes nothing at all for a source with no
    /// channels, so an analog-less device would otherwise produce an empty file.
    /// </param>
    /// <param name="deviceName">
    /// The device name to build channel keys from. Null or blank becomes
    /// <see cref="DefaultDeviceName"/>, since a log file carries no device name.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="analogChannelCount"/> is negative.
    /// </exception>
    public SdCardLogSampleSource(
        IAsyncEnumerable<SdCardLogEntry> samples,
        string? deviceSerialNumber,
        int analogChannelCount,
        string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (analogChannelCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analogChannelCount),
                analogChannelCount,
                "The analog channel count cannot be negative.");
        }

        _samples = samples;

        var name = string.IsNullOrWhiteSpace(deviceName) ? DefaultDeviceName : deviceName;
        var serial = string.IsNullOrWhiteSpace(deviceSerialNumber) ? Unknown : deviceSerialNumber;

        AnalogChannelCount = analogChannelCount;
        _channels = new List<ChannelDescriptor>(analogChannelCount + 1);
        _analogKeys = new string[analogChannelCount];

        for (var i = 0; i < analogChannelCount; i++)
        {
            var descriptor = new ChannelDescriptor(name, serial, $"AI{i}", ChannelType.Analog);
            _channels.Add(descriptor);
            _analogKeys[i] = descriptor.Key;
        }

        var digital = new ChannelDescriptor(name, serial, DigitalChannelName, ChannelType.Digital);
        _channels.Add(digital);
        _digitalKey = digital.Key;
    }

    /// <summary>
    /// Gets the number of analog columns this source was built with, excluding the digital column.
    /// </summary>
    /// <remarks>
    /// Read with <see cref="DroppedAnalogColumns"/> this says how wide the log really was: an entry
    /// carrying <see cref="AnalogChannelCount"/> + <see cref="DroppedAnalogColumns"/> values was
    /// seen while only <see cref="AnalogChannelCount"/> columns existed to hold them.
    /// </remarks>
    public int AnalogChannelCount { get; }

    /// <summary>
    /// Gets the number of log entries read from the file. Entries, not CSV lines — see
    /// <see cref="RowCount"/>.
    /// </summary>
    public long SampleCount { get; private set; }

    /// <summary>
    /// Gets the number of distinct consecutive timestamps seen, which is the number of CSV data
    /// lines a default export writes.
    /// </summary>
    /// <remarks>
    /// A row is a timestamp, not a sample: <see cref="CsvExporter"/> collapses every consecutive
    /// <see cref="SampleRow"/> sharing a timestamp into one line, and every channel of one log
    /// entry shares that entry's timestamp exactly. Reporting <see cref="SampleCount"/> as the line
    /// count would therefore over-report by the channel count. This tracks the exporter's own flush
    /// rule, so it holds even when consecutive entries repeat a timestamp. It does <b>not</b>
    /// describe an averaged export (<see cref="CsvExportOptions.AverageWindow"/>), which writes one
    /// line per window instead.
    /// </remarks>
    public long RowCount { get; private set; }

    /// <summary>
    /// Gets the largest number of analog values a single entry carried beyond
    /// <see cref="AnalogChannelCount"/>, and which therefore had nowhere to go.
    /// </summary>
    /// <remarks>
    /// Non-zero means the CSV is missing columns the log had data for — usually because the log
    /// carries no status message and the channel count came from a fallback that does not match the
    /// recording device. It is the widest overflow seen rather than a running total, so it reads as
    /// "this many columns short" instead of a number that grows with the file's length. It is
    /// surfaced rather than swallowed: an export that quietly drops channels gives the caller no
    /// way to notice.
    /// </remarks>
    public int DroppedAnalogColumns { get; private set; }

    /// <summary>
    /// Gets the number of entries whose timestamp went backwards relative to the entry before it.
    /// </summary>
    /// <remarks>
    /// <see cref="ISampleSource.StreamSamples"/> is contractually ascending, and a log normally
    /// satisfies it: <see cref="SdCardTimestampReconstructor"/> rebuilds a monotonic time across
    /// the device counter's rollover. This counts the exception rather than assuming it away — a
    /// rollover mis-detection, or a log spliced from two recordings, produces a CSV whose time
    /// column goes backwards. Rows are still exported, because losing data is worse than exporting
    /// it out of order, and a caller that cannot tolerate it can see that it happened.
    /// </remarks>
    public long NonMonotonicEntryCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ChannelDescriptor> GetChannels() => _channels;

    /// <summary>
    /// Always returns 0 — the length of a log is not known without reading it.
    /// </summary>
    /// <remarks>
    /// This is exactly the "count is unavailable" case
    /// <see cref="ISampleSource.GetSampleCountAsync"/> documents, and returning 0 is what makes
    /// <see cref="CsvExporter"/> skip percentage progress rather than divide by a total it does not
    /// have. Counting first would mean reading the whole log before exporting it — twice the I/O,
    /// and on a stream-backed session an extra pass over the one read cursor it has. Read
    /// <see cref="SampleCount"/> or <see cref="RowCount"/> as the export runs instead.
    /// </remarks>
    /// <param name="cancellationToken">Unused; the answer needs no work.</param>
    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    /// <summary>
    /// Streams the log entries as export rows — one row per analog column that has a value, then
    /// one for the digital port — updating the counters as it goes.
    /// </summary>
    /// <remarks>
    /// An entry with fewer analog values than there are columns leaves the remaining columns empty
    /// for that timestamp rather than filling them with zeros, which the exporter writes as blank
    /// cells; an entry with more is truncated to the columns that exist and reported through
    /// <see cref="DroppedAnalogColumns"/>. Values are passed through as parsed, since the SD
    /// parsers have already applied the log's calibration and scaling.
    /// <para>
    /// The counters accumulate over the lifetime of the source rather than resetting per call, so
    /// a source enumerated twice reports both passes. One source per export is the normal use.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Ends the enumeration, and with it the read of the log.</param>
    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long? previousTicks = null;

        await foreach (var entry in _samples.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            SampleCount++;

            var ticks = entry.Timestamp.Ticks;

            // Mirrors the exporter's own flush rule — a new line whenever the timestamp changes —
            // so the count matches the file line for line.
            if (previousTicks != ticks)
            {
                RowCount++;
            }

            if (previousTicks is { } previous && ticks < previous)
            {
                NonMonotonicEntryCount++;
            }

            previousTicks = ticks;

            var values = entry.AnalogValues;
            var overflow = values.Count - _analogKeys.Length;
            if (overflow > DroppedAnalogColumns)
            {
                DroppedAnalogColumns = overflow;
            }

            var mapped = Math.Min(values.Count, _analogKeys.Length);
            for (var i = 0; i < mapped; i++)
            {
                yield return new SampleRow(ticks, _analogKeys[i], values[i]);
            }

            yield return new SampleRow(ticks, _digitalKey, entry.DigitalData);
        }
    }
}
