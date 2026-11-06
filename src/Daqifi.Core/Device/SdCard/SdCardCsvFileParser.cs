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
///   <item><description>Data rows with interleaved per-channel timestamp/value pairs.</description></item>
/// </list>
/// </para>
/// <para>
/// An analog value is not always a raw ADC count. Firmware writes volts with a decimal point,
/// and writes raw ADC counts and millivolts as whole numbers. The parser therefore reads the
/// volts case from the file itself and leaves those values alone, and takes
/// <see cref="SdCardParseOptions.IntegerValueUnits"/> for the whole-number case. Every sample
/// reports the unit that was applied in
/// <see cref="SdCardLogEntry.SourceAnalogValueUnit"/>. See
/// <see cref="SdCardAnalogValueUnitRule"/> for the rule and why it is read from the file.
/// </para>
/// <para>
/// A row that mixes the two forms states no single unit, so enumerating
/// <see cref="SdCardLogSession.Samples"/> throws
/// <see cref="System.IO.InvalidDataException"/> at that row instead of guessing.
/// </para>
/// <para>
/// A non-finite reading — firmware's <c>printf("%f")</c> writes <c>nan</c>, <c>inf</c> or
/// <c>-inf</c> for one — is reported as the NaN or infinity it is, rather than dropped or
/// turned into a zero, and it states no unit, so it neither decides a row's unit nor refuses
/// the row. A data row the parser cannot read at all is skipped so the rest of the file still
/// imports, and counted in <see cref="SdCardParseProgress.RowsSkipped"/> so that the loss is
/// visible to a caller watching <see cref="SdCardParseOptions.Progress"/>.
/// </para>
/// <para>
/// A data row must carry exactly as many fields as the column header does. A log that is stopped
/// rather than closed can end part-way through a row, and such a row used to pass as a sample
/// with fewer values — exported as a row of empty cells, which a reader that reads an empty cell
/// as zero sees as every remaining channel dropping to zero for one sample (issue #905). The last
/// row of a file is dropped as the truncated tail and reported in
/// <see cref="SdCardParseProgress.TruncatedFinalRow"/>, which is what tells a clean end from a
/// cut one; a mismatched row anywhere else is a parse problem and is counted in
/// <see cref="SdCardParseProgress.RowsSkipped"/> with the other rows the parser cannot read.
/// </para>
/// <para>
/// The header names only the channels the recording had enabled, and it names them by number,
/// so a file of channel 7 alone reads <c>ain7_ts,ain7_val</c>. Each column that holds a raw ADC
/// code is therefore scaled with the calibration, port range, and internal scale of the channel
/// its own header names, not of the channel sitting at its position. A column header that states
/// no channel number fails the parse with an <see cref="InvalidDataException"/>, because scaling
/// it by position would report a wrong voltage and say nothing about it.
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SdCardParseOptions.BufferSize"/> is not greater than zero.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An analog column header states no channel number, so the column cannot be matched to a
    /// channel's calibration.
    /// </exception>
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
        SdCardParseOptions.ThrowIfInvalid(options);

        return await SdCardTextFileSource.ParseAsync(
            fileStream,
            ct,
            (openLines, totalBytes, token) => BuildSessionAsync(openLines, totalBytes, fileName, options, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an SD card CSV log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the CSV log file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SdCardParseOptions.BufferSize"/> is not greater than zero.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An analog column header states no channel number, so the column cannot be matched to a
    /// channel's calibration.
    /// </exception>
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
        SdCardParseOptions.ThrowIfInvalid(options);

        return await SdCardTextFileSource.ParseFileAsync(
            filePath,
            options.BufferSize,
            ct,
            (openLines, totalBytes, token) => BuildSessionAsync(openLines, totalBytes, Path.GetFileName(filePath), options, token))
            .ConfigureAwait(false);
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
                SdCardTextFileSource.EmptySamples());
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
                SdCardTextFileSource.EmptySamples())
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
            options.IntegerValueUnits,
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
    /// <param name="AnalogChannelIndices">
    /// The channel number each analog column pair carries, read from the header name
    /// (<c>ain7_ts</c> is channel 7). Empty when the file states no column header at all.
    /// </param>
    private sealed record CsvColumnLayout(
        int AnalogPairCount,
        bool HasDigitalPair,
        IReadOnlyList<int> AnalogChannelIndices);

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
        var analogChannelIndices = new List<int>();

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
                    // Count channel pairs, identify digital columns, and read the channel
                    // number out of every analog column name. The firmware names only the
                    // enabled channels, so a column's position is not its channel number.
                    var cols = line.Split(',');
                    var totalPairs = cols.Length / 2;

                    for (var p = 0; p < totalPairs; p++)
                    {
                        // Trimmed once, so that a hand-edited file with a space after each
                        // comma classifies its columns exactly as the firmware's own does.
                        var nameCol = cols[p * 2].Trim(); // e.g., "ain0_ts" or "dio_ts"
                        if (nameCol.StartsWith("dio", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDigitalPair = true;
                            digitalChannelCount = 1;
                            continue;
                        }

                        if (!TryParseAnalogChannelIndex(nameCol, out var channelIndex))
                        {
                            throw new InvalidDataException(
                                $"SD card CSV column header '{nameCol}' names no analog channel. " +
                                "Expected 'ain<N>_ts', 'ch<N>_ts', or a 'dio' pair. Calibration is " +
                                "applied per channel number, so this column cannot be scaled.");
                        }

                        analogChannelCount++;
                        analogChannelIndices.Add(channelIndex);
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
            new CsvColumnLayout(analogChannelCount, hasDigitalPair, analogChannelIndices));
    }

    /// <summary>
    /// Reads the channel number out of an analog column name: <c>ain7_ts</c> and <c>ch7_val</c>
    /// are both channel 7. The firmware names only the enabled channels
    /// (<c>csv_encoder.c</c>), so this number — not the column's position — says which
    /// channel's calibration, port range, and internal scale the column needs.
    /// </summary>
    /// <param name="columnName">One column name from the header row.</param>
    /// <param name="channelIndex">The channel number, when the name states one.</param>
    /// <returns><c>true</c> when the name states a channel number; <c>false</c> otherwise.</returns>
    private static bool TryParseAnalogChannelIndex(string columnName, out int channelIndex)
    {
        channelIndex = -1;

        var name = columnName.Trim();

        // Drop the "_ts" / "_val" suffix, so only the channel token is left.
        var underscore = name.IndexOf('_');
        var token = underscore >= 0 ? name[..underscore] : name;

        int digitsStart;
        if (token.StartsWith("ain", StringComparison.OrdinalIgnoreCase))
        {
            digitsStart = 3;
        }
        else if (token.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
        {
            digitsStart = 2;
        }
        else
        {
            return false;
        }

        var digits = token[digitsStart..];

        // NumberStyles.None rejects a sign, a decimal point, and surrounding space, so only a
        // plain channel number gets through.
        if (digits.Length == 0
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        channelIndex = parsed;
        return true;
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
        SdCardIntegerValueUnits? integerValueUnits,
        IProgress<SdCardParseProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestampReconstructor =
            SdCardTimestampReconstructor.ForTimestampFrequency(config.TimestampFrequency);

        var linesProcessed = 0;
        var bytesRead = 0L;
        var totalBytes = totalBytesProvider();
        var skipped = 0;
        var rowsSkipped = 0;
        var truncatedFinalRow = false;

        // The header's own column count, or 0 when the file states no column header and there is
        // nothing to match a row against. Every pair is a timestamp and a value, including the
        // digital pair.
        var expectedColumnCount =
            (columnLayout.AnalogPairCount + (columnLayout.HasDigitalPair ? 1 : 0)) * 2;

        // A row whose field count did not match, held back until the next row says what it was. A
        // held row at the end of the file is the truncated tail; a held row with another row after
        // it was short in the middle of the file, which is a parse problem instead.
        var heldMismatch = false;

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

            // Another row arrived, so the held one was not the file's last: it was a short row in
            // the middle, which is a parse problem like any other row the parser cannot read.
            if (heldMismatch)
            {
                heldMismatch = false;
                rowsSkipped++;
            }

            if (expectedColumnCount > 0 && CountFields(line) != expectedColumnCount)
            {
                // Hold it rather than yield it. Nothing here can tell yet whether this is the
                // truncated tail of a stopped recording or a short row mid-file, and the two are
                // counted differently.
                heldMismatch = true;
                continue;
            }

            var parsed = TryParseCsvDataRow(line, columnLayout);
            if (parsed == null)
            {
                // Skip the row so the rest of the file still imports, but count it: these are
                // readings the caller never receives, and a silent drop costs a user data
                // without telling them (closes #884).
                rowsSkipped++;
                continue;
            }

            var (rowTimestamp, rawAnalogValues, digitalData, perChannelTimestamps, finiteValueCount, decimalFormCount) = parsed.Value;

            // Read the row's unit from the file, then convert for that unit. A value the device
            // already calibrated — volts or millivolts — must not go through the ADC-code
            // conversion as well (closes #848). Only the values that state a unit are counted:
            // a non-finite reading states none, so it cannot make a volts row look mixed
            // (closes #883).
            var valueUnit = SdCardAnalogValueUnitRule.ResolveRowUnit(
                finiteValueCount,
                decimalFormCount,
                integerValueUnits,
                line);

            // Only a raw ADC code needs that conversion, and the conversion uses each column's
            // own channel calibration, named by the header rather than implied by the column's
            // position (#849).
            var analogValues = valueUnit switch
            {
                SdCardAnalogValueUnit.Volts => rawAnalogValues,
                SdCardAnalogValueUnit.Millivolts => SdCardAnalogValueUnitRule.MillivoltsToVolts(rawAnalogValues),
                _ => SdCardAnalogScaling.ScaleRawAnalogValues(
                    rawAnalogValues,
                    config,
                    columnLayout.AnalogChannelIndices)
            };

            // Reconstruct absolute timestamp using first channel timestamp
            var hasDeviceTimestamp = timestampReconstructor.HasDeviceClock;
            var absoluteTime = hasDeviceTimestamp
                ? timestampReconstructor.Advance(rowTimestamp, baseTime)
                : baseTime;

            yield return new SdCardLogEntry(absoluteTime, analogValues, digitalData, perChannelTimestamps)
            {
                HasDeviceTimestamp = hasDeviceTimestamp,
                SourceAnalogValueUnit = valueUnit
            };

            // Report progress every 100 lines for efficiency
            if (linesProcessed % 100 == 0 && progress != null)
            {
                progress.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed)
                {
                    RowsSkipped = rowsSkipped,
                    TruncatedFinalRow = truncatedFinalRow
                });
            }
        }

        // Nothing followed the held row, so it was the file's last: the truncated tail of a
        // recording that was stopped rather than closed.
        if (heldMismatch)
        {
            truncatedFinalRow = true;
        }

        // Final progress report. This one always goes out, so a caller that watches progress
        // learns the file's whole skipped-row count even when the file is shorter than the
        // reporting interval.
        progress?.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed)
        {
            RowsSkipped = rowsSkipped,
            TruncatedFinalRow = truncatedFinalRow
        });
    }

    /// <summary>
    /// Counts a line's comma-separated fields the way the row parser splits it, without
    /// allocating the split. One more than the number of commas, so a line with a trailing comma
    /// counts the empty field after it — which is a field the header has no column for, and that
    /// is the point.
    /// </summary>
    /// <param name="line">One line of the file, as the reader hands it over.</param>
    /// <returns>The number of fields the line holds.</returns>
    private static int CountFields(string line)
    {
        var fields = 1;
        foreach (var c in line)
        {
            if (c == ',')
            {
                fields++;
            }
        }

        return fields;
    }

    /// <summary>
    /// Parses one analog value token, accepting both spellings of a non-finite reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Firmware writes a non-finite reading with <c>printf("%f")</c>, which produces C's
    /// <c>nan</c>, <c>inf</c> and <c>-inf</c>. <see cref="double.TryParse(string?, NumberStyles, IFormatProvider?, out double)"/>
    /// takes <c>nan</c> and <c>Infinity</c> but not <c>inf</c>, so a row holding one used to
    /// fail the parse and be dropped whole — the reading, the other channels' readings on that
    /// row, and its timestamp, with nothing said about it (closes #884).
    /// </para>
    /// <para>
    /// So <c>inf</c> is accepted here the way <c>nan</c> already was, with an optional sign and
    /// without regard to case. The value is reported as the infinity it is, which is what tells
    /// a caller the channel saturated rather than read zero.
    /// </para>
    /// </remarks>
    /// <param name="token">One value token, as the file wrote it.</param>
    /// <param name="value">The parsed value.</param>
    /// <returns><c>true</c> when the token is a value this parser understands.</returns>
    private static bool TryParseAnalogValue(string token, out double value)
    {
        if (double.TryParse(token, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return TryParseCInfinity(token, out value);
    }

    /// <summary>
    /// Parses C's <c>inf</c> spelling of an infinity: the word <c>inf</c> or <c>infinity</c>,
    /// in any case, optionally signed, with the surrounding whitespace
    /// <see cref="NumberStyles.Float"/> would itself have allowed.
    /// </summary>
    /// <param name="token">One value token, as the file wrote it.</param>
    /// <param name="value">
    /// <see cref="double.PositiveInfinity"/> or <see cref="double.NegativeInfinity"/> on
    /// success; <c>0</c> otherwise.
    /// </param>
    /// <returns><c>true</c> when the token is C's spelling of an infinity.</returns>
    private static bool TryParseCInfinity(string token, out double value)
    {
        value = 0;

        var text = token.AsSpan().Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        var negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        if (!text.Equals("inf", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = negative ? double.NegativeInfinity : double.PositiveInfinity;
        return true;
    }

    /// <summary>
    /// Parses a firmware CSV data row with interleaved per-channel timestamp/value pairs.
    /// Separates analog channel pairs from the digital I/O pair based on the column layout.
    /// Format: ain0_ts,ain0_val,...,dio_ts,dio_val
    /// <para>
    /// Also counts how many analog values the row wrote with a decimal point or an exponent.
    /// That count is what says whether the row holds volts, and it has to be taken here,
    /// from the text, because <c>245</c> and <c>245.0</c> parse to the same <c>double</c>
    /// while meaning different units. Non-finite readings are left out of both counts, since
    /// they state no unit at all — see <see cref="SdCardAnalogValueUnitRule"/>.
    /// </para>
    /// </summary>
    private static (uint rowTimestamp, IReadOnlyList<double> analogValues, uint digitalData, IReadOnlyList<uint> perChannelTimestamps, int finiteValueCount, int decimalFormCount)?
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
            var finiteValueCount = 0;
            var decimalFormCount = 0;

            // Parse analog channel pairs
            for (var ch = 0; ch < analogPairCount; ch++)
            {
                var tsCol = columns[ch * 2];
                var valCol = columns[ch * 2 + 1];

                if (!uint.TryParse(tsCol, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
                {
                    return null;
                }

                if (!TryParseAnalogValue(valCol, out var val))
                {
                    return null;
                }

                perChannelTimestamps.Add(ts);
                analogValues.Add(val);

                // A NaN or an infinity is not written in either unit form, so it is evidence
                // for neither and is kept out of both counts.
                if (SdCardAnalogValueUnitRule.IsUnitlessValue(val))
                {
                    continue;
                }

                finiteValueCount++;

                if (SdCardAnalogValueUnitRule.IsDecimalForm(valCol))
                {
                    decimalFormCount++;
                }
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

            return (rowTimestamp, analogValues, digitalData, perChannelTimestamps, finiteValueCount, decimalFormCount);
        }
        catch
        {
            return null;
        }
    }

}
