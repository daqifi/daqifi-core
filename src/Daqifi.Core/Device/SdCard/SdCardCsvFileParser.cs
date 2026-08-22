using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Parses SD card log <c>.csv</c> files produced by DAQiFi firmware.
/// <para>
/// The firmware CSV format consists of:
/// <list type="bullet">
///   <item><description>Up to three <c>#</c>-prefixed comment lines containing device metadata
///   (device name, serial number, and timestamp tick rate).</description></item>
///   <item><description>A column header row: <c>ain0_ts,ain0_val,ain1_ts,ain1_val,...,dio_ts,dio_val</c></description></item>
///   <item><description>Data rows with interleaved per-channel timestamp/value pairs.
///   Analog values are raw ADC counts that require scaling.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SdCardCsvFileParser
{
    /// <summary>
    /// Parses an SD card CSV log file from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="fileStream">A readable stream containing the CSV log data.</param>
    /// <param name="fileName">The file name (used for metadata and date extraction).</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    /// <remarks>
    /// The session reads <paramref name="fileStream"/> lazily: keep the stream open and do not
    /// read from it yourself until you have finished enumerating
    /// <see cref="SdCardLogSession.Samples"/>. A seekable stream is re-read from its starting
    /// position on each enumeration, and only one enumeration may be in flight at a time; a
    /// forward-only stream cannot be re-read, so its contents are decoded up front instead.
    /// </remarks>
    public async Task<SdCardLogSession> ParseAsync(
        Stream fileStream,
        string fileName,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        ArgumentNullException.ThrowIfNull(fileName);

        options ??= new SdCardParseOptions();

        var source = SdCardParseSource.TryCreate(fileStream);
        if (source != null)
        {
            return await BuildSessionAsync(
                token => SdCardTextLineReader.ReadLinesAsync(source, token),
                () => source.TotalBytes,
                fileName,
                options,
                ct).ConfigureAwait(false);
        }

        // A forward-only stream can only be read once, so it has to be read up front.
        // Callers that want the streaming parse hand the parser a seekable stream or a path.
        var lines = new List<SdCardLogLine>();
        await foreach (var line in SdCardTextLineReader.ReadLinesAsync(fileStream, ct).ConfigureAwait(false))
        {
            lines.Add(line);
        }

        return await BuildSessionAsync(
            _ => SdCardTextLineReader.ToAsyncEnumerable(lines),
            () => lines.Count > 0 ? lines[^1].BytesRead : 0,
            fileName,
            options,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an SD card CSV log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the CSV log file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    /// <remarks>
    /// The returned session opens its own read of the file each time
    /// <see cref="SdCardLogSession.Samples"/> is enumerated, so the file must still exist then.
    /// </remarks>
    public async Task<SdCardLogSession> ParseFileAsync(
        string filePath,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        options ??= new SdCardParseOptions();

        var source = SdCardParseSource.ForFile(filePath, options.BufferSize);

        return await BuildSessionAsync(
            token => SdCardTextLineReader.ReadLinesAsync(source, token),
            () => source.TotalBytes,
            Path.GetFileName(filePath),
            options,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the header region, then hands the session a sample iterator that re-reads the
    /// file lazily rather than holding its lines in memory.
    /// </summary>
    private static async Task<SdCardLogSession> BuildSessionAsync(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        Func<long> totalBytes,
        string fileName,
        SdCardParseOptions options,
        CancellationToken ct)
    {
        var fileCreatedDate = options.SessionStartTime
                              ?? SdCardFileListParser.TryParseDateFromLogFileName(fileName);

        var header = await ReadHeaderAsync(openLines, ct).ConfigureAwait(false);

        if (!header.SawAnyLine)
        {
            // Empty file
            return new SdCardLogSession(
                fileName,
                fileCreatedDate,
                null,
                EmptySamples());
        }

        var config = SdCardConfigurationMerge.Merge(header.Config, options.ConfigurationOverride);

        var (timestampFrequency, timestampSource) = SdCardTimestampFrequencyResolver.Resolve(
            header.Config.TimestampFrequency,
            options.ConfigurationOverride?.TimestampFrequency ?? 0u,
            options.FallbackTimestampFrequency);

        config = config with { TimestampFrequency = timestampFrequency };

        if (header.DataStartIndex < 0)
        {
            // Only header, no data
            return new SdCardLogSession(
                fileName,
                fileCreatedDate,
                config,
                EmptySamples())
            {
                TimestampFrequency = timestampFrequency,
                TimestampFrequencySource = timestampSource
            };
        }

        var samples = ParseCsvLines(
            openLines,
            header.DataStartIndex,
            totalBytes,
            config,
            header.Layout,
            // Anchored once, here, rather than inside the iterator: a session whose samples can
            // be enumerated more than once must not shift its timestamps between reads when the
            // file name carries no date.
            fileCreatedDate ?? DateTime.UtcNow,
            options.Progress,
            ct);

        return new SdCardLogSession(fileName, fileCreatedDate, config, samples)
        {
            TimestampFrequency = timestampFrequency,
            TimestampFrequencySource = timestampSource
        };
    }

    /// <summary>
    /// Describes the column layout parsed from the CSV header row.
    /// </summary>
    /// <param name="AnalogPairCount">Number of analog channel column pairs (ts + val).</param>
    /// <param name="HasDigitalPair">Whether the last column pair is a digital I/O pair (dio_ts, dio_val).</param>
    private sealed record CsvColumnLayout(int AnalogPairCount, bool HasDigitalPair);

    /// <summary>
    /// The result of the header scan: everything the sample pass needs to know before it
    /// starts reading data rows.
    /// </summary>
    /// <param name="SawAnyLine">Whether the file contained any non-blank line at all.</param>
    /// <param name="DataStartIndex">Index of the first data row among the non-blank lines, or -1 when the file has no data rows.</param>
    /// <param name="Config">Device metadata stated by the file itself.</param>
    /// <param name="Layout">The column layout stated by the column-header row.</param>
    private sealed record CsvHeader(
        bool SawAnyLine,
        int DataStartIndex,
        SdCardDeviceConfiguration Config,
        CsvColumnLayout Layout);

    /// <summary>
    /// Reads the header region — comment metadata and the column header — and locates the
    /// first data row. Stops at that row, so this never reads more than the file's preamble.
    /// </summary>
    private static async Task<CsvHeader> ReadHeaderAsync(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        CancellationToken ct)
    {
        string? deviceName = null;
        string? serialNumber = null;

        // File-stated frequency only. The device override and the caller's fallback are
        // applied afterwards, in that order, so that a connected device's real clock beats a
        // fallback guess instead of losing to it.
        var timestampFreq = 0u;
        var analogChannelCount = 0;
        var digitalChannelCount = 0;
        var hasDigitalPair = false;

        var index = 0;
        var dataStartIndex = -1;
        var sawAnyLine = false;
        var headerDone = false;

        await foreach (var entry in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            var line = entry.Text;
            sawAnyLine = true;

            if (!headerDone && line.StartsWith('#'))
            {
                // # Device: Nyquist 1
                // # Serial Number: 7E2815916200E898
                // # Timestamp Tick Rate: 50000000 Hz
                var content = line[1..].Trim();
                if (content.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
                {
                    deviceName = content["Device:".Length..].Trim();
                }
                else if (content.StartsWith("Serial Number:", StringComparison.OrdinalIgnoreCase))
                {
                    serialNumber = content["Serial Number:".Length..].Trim();
                }
                else if (content.StartsWith("Timestamp Tick Rate:", StringComparison.OrdinalIgnoreCase))
                {
                    var rateStr = content["Timestamp Tick Rate:".Length..].Trim();
                    // Remove " Hz" suffix if present
                    var spaceIdx = rateStr.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        rateStr = rateStr[..spaceIdx];
                    }

                    if (uint.TryParse(rateStr, NumberStyles.None, CultureInfo.InvariantCulture, out var rate))
                    {
                        timestampFreq = rate;
                    }
                }

                index++;
                continue;
            }

            if (IsColumnHeaderLine(line))
            {
                if (!headerDone)
                {
                    // Column header: ain0_ts,ain0_val,ain1_ts,ain1_val,...,dio_ts,dio_val
                    // Count channel pairs and identify digital columns
                    var cols = line.Split(',');
                    var totalPairs = cols.Length / 2;

                    // Check each pair to distinguish analog from digital
                    for (var p = 0; p < totalPairs; p++)
                    {
                        var nameCol = cols[p * 2]; // e.g., "ain0_ts" or "dio_ts"
                        if (nameCol.StartsWith("dio", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDigitalPair = true;
                            digitalChannelCount = 1;
                        }
                        else
                        {
                            analogChannelCount++;
                        }
                    }

                    // Only the first column header states the layout; anything after it is
                    // no longer part of the metadata region.
                    headerDone = true;
                }

                index++;
                continue;
            }

            if (IsHeaderNoiseLine(line) || line.StartsWith('#'))
            {
                // Malformed header-region noise (e.g. a stray "ch") before the real
                // column header. Skip it and keep scanning so the real header — and its
                // channel layout — isn't lost.
                index++;
                continue;
            }

            // First data row.
            dataStartIndex = index;
            break;
        }

        var config = new SdCardDeviceConfiguration(
            AnalogPortCount: analogChannelCount,
            DigitalPortCount: digitalChannelCount,
            TimestampFrequency: timestampFreq,
            DeviceSerialNumber: serialNumber,
            DevicePartNumber: deviceName,
            FirmwareRevision: null,
            CalibrationValues: null);

        return new CsvHeader(
            sawAnyLine,
            dataStartIndex,
            config,
            new CsvColumnLayout(analogChannelCount, hasDigitalPair));
    }

    /// <summary>
    /// Determines whether a line is a CSV column header
    /// (e.g. <c>ain0_ts,ain0_val,...,dio_ts,dio_val</c> or a <c>ch...</c>/<c>ain...</c> variant).
    /// Used by <see cref="ReadHeaderAsync"/> both to read the layout and to find where the data
    /// starts, so the two agree on what a header looks like. The <c>line.Length &gt; 2</c> guard protects the <c>line[2]</c>
    /// access: <c>StartsWith("ch")</c> only guarantees length &gt;= 2, so a bare "ch" line would
    /// otherwise throw <see cref="IndexOutOfRangeException"/> (closes #365).
    /// </summary>
    private static bool IsColumnHeaderLine(string line) =>
        line.Contains("_ts,", StringComparison.OrdinalIgnoreCase) ||
        (line.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && line.Length > 2 && !char.IsDigit(line[2])) ||
        line.StartsWith("ain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a non-comment line is malformed header-region noise: it starts with
    /// "ch" (case-insensitive) but is not a valid column header (e.g. a bare "ch"). A genuine
    /// data row starts with a numeric timestamp, never "ch", so such a line is never data.
    /// Both the header parser and the data-start scan skip it, so a stray "ch" before the real
    /// column header can neither hide the header nor shift the data-start onto a bad row.
    /// </summary>
    private static bool IsHeaderNoiseLine(string line) =>
        line.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && !IsColumnHeaderLine(line);

    private static async IAsyncEnumerable<SdCardLogEntry> ParseCsvLines(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        int dataStartIndex,
        Func<long> totalBytesProvider,
        SdCardDeviceConfiguration config,
        CsvColumnLayout columnLayout,
        DateTime baseTime,
        IProgress<SdCardParseProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestampFreq = config.TimestampFrequency;
        var tickPeriod = timestampFreq > 0 ? 1.0 / timestampFreq : 0.0;

        uint? previousTimestamp = null;
        var elapsedSeconds = 0.0;
        var linesProcessed = 0;
        var bytesRead = 0L;
        var totalBytes = totalBytesProvider();
        var skipped = 0;

        await foreach (var entry in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            // The preamble still counts towards bytes read — it is part of the file the caller
            // is waiting on — so progress can reach the file's length.
            bytesRead = entry.BytesRead;

            // Re-reading the file means walking the header region again; it is bounded by the
            // preamble, and skipping it costs nothing next to decoding a data row.
            if (skipped < dataStartIndex)
            {
                skipped++;
                continue;
            }

            ct.ThrowIfCancellationRequested();

            var line = entry.Text;
            linesProcessed++;

            var parsed = TryParseCsvDataRow(line, columnLayout);
            if (parsed == null)
            {
                // Skip malformed lines
                continue;
            }

            var (rowTimestamp, rawAnalogValues, digitalData, perChannelTimestamps) = parsed.Value;

            // Scale raw ADC values using device calibration
            var analogValues = SdCardAnalogScaling.ScaleRawAnalogValues(rawAnalogValues, config);

            // Reconstruct absolute timestamp using first channel timestamp
            var absoluteTime = baseTime;
            var hasDeviceTimestamp = tickPeriod > 0;
            if (hasDeviceTimestamp)
            {
                if (previousTimestamp == null)
                {
                    previousTimestamp = rowTimestamp;
                }
                else
                {
                    var delta = SdCardTickDelta.Compute(previousTimestamp.Value, rowTimestamp);
                    elapsedSeconds += delta * tickPeriod;
                    previousTimestamp = rowTimestamp;
                }

                absoluteTime = baseTime.AddSeconds(elapsedSeconds);
            }

            yield return new SdCardLogEntry(absoluteTime, analogValues, digitalData, perChannelTimestamps)
            {
                HasDeviceTimestamp = hasDeviceTimestamp
            };

            // Report progress every 100 lines for efficiency
            if (linesProcessed % 100 == 0 && progress != null)
            {
                progress.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed));
            }
        }

        // Final progress report
        progress?.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed));
    }

    /// <summary>
    /// Parses a firmware CSV data row with interleaved per-channel timestamp/value pairs.
    /// Separates analog channel pairs from the digital I/O pair based on the column layout.
    /// Format: ain0_ts,ain0_val,...,dio_ts,dio_val
    /// </summary>
    private static (uint rowTimestamp, IReadOnlyList<double> analogValues, uint digitalData, IReadOnlyList<uint> perChannelTimestamps)?
        TryParseCsvDataRow(string line, CsvColumnLayout layout)
    {
        try
        {
            var columns = line.Split(',');
            // Must have at least one channel pair (2 columns: ts + val)
            if (columns.Length < 2 || columns.Length % 2 != 0)
            {
                return null;
            }

            var totalPairs = columns.Length / 2;
            var analogPairCount = layout.AnalogPairCount > 0
                ? Math.Min(layout.AnalogPairCount, totalPairs)
                : (layout.HasDigitalPair ? totalPairs - 1 : totalPairs);

            var analogValues = new List<double>(analogPairCount);
            var perChannelTimestamps = new List<uint>(analogPairCount);

            // Parse analog channel pairs
            for (var ch = 0; ch < analogPairCount; ch++)
            {
                var tsCol = columns[ch * 2];
                var valCol = columns[ch * 2 + 1];

                if (!uint.TryParse(tsCol, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
                {
                    return null;
                }

                if (!double.TryParse(valCol, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var val))
                {
                    return null;
                }

                perChannelTimestamps.Add(ts);
                analogValues.Add(val);
            }

            // Parse digital I/O pair if present (last pair)
            uint digitalData = 0;
            uint dioTimestamp = 0;
            if (layout.HasDigitalPair && totalPairs > analogPairCount)
            {
                var dioIndex = analogPairCount;
                var dioTsCol = columns[dioIndex * 2];
                var dioValCol = columns[dioIndex * 2 + 1];
                uint.TryParse(dioTsCol, NumberStyles.None, CultureInfo.InvariantCulture, out dioTimestamp);
                if (uint.TryParse(dioValCol, NumberStyles.None, CultureInfo.InvariantCulture, out var dioVal))
                {
                    digitalData = dioVal;
                }
            }

            // Use first analog channel's timestamp, or fall back to dio timestamp
            var rowTimestamp = perChannelTimestamps.Count > 0
                ? perChannelTimestamps[0]
                : dioTimestamp;

            if (perChannelTimestamps.Count == 0 && dioTimestamp == 0 && !layout.HasDigitalPair)
            {
                return null;
            }

            return (rowTimestamp, analogValues, digitalData, perChannelTimestamps);
        }
        catch
        {
            return null;
        }
    }

#pragma warning disable CS1998 // Async iterator: yield break requires async; no real awaits.
    private static async IAsyncEnumerable<SdCardLogEntry> EmptySamples()
    {
        yield break;
    }
#pragma warning restore CS1998
}
