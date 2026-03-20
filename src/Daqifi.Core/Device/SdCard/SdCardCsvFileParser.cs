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
    public async Task<SdCardLogSession> ParseAsync(
        Stream fileStream,
        string fileName,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        ArgumentNullException.ThrowIfNull(fileName);

        options ??= new SdCardParseOptions();

        var fileCreatedDate = options.SessionStartTime
                              ?? SdCardFileListParser.TryParseDateFromLogFileName(fileName);

        // Read all lines into memory (similar to protobuf parser reading all messages)
        var lines = new List<string>();
        using (var reader = new StreamReader(fileStream, leaveOpen: true))
        {
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        if (lines.Count == 0)
        {
            // Empty file
            return new SdCardLogSession(
                fileName,
                fileCreatedDate,
                null,
                EmptySamples());
        }

        var (headerConfig, columnLayout) = ParseHeader(lines, options);
        var config = MergeConfiguration(headerConfig, options.ConfigurationOverride);

        // Find the index of the first data row (after comments and column header)
        var dataStartIndex = FindDataStartIndex(lines);

        if (dataStartIndex >= lines.Count)
        {
            // Only header, no data
            return new SdCardLogSession(
                fileName,
                fileCreatedDate,
                config,
                EmptySamples());
        }

        var samples = ParseCsvLines(
            lines,
            dataStartIndex,
            config,
            columnLayout,
            fileCreatedDate,
            options);

        return new SdCardLogSession(fileName, fileCreatedDate, config, samples);
    }

    /// <summary>
    /// Parses an SD card CSV log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the CSV log file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public async Task<SdCardLogSession> ParseFileAsync(
        string filePath,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: options?.BufferSize ?? 64 * 1024,
            useAsync: true);

        return await ParseAsync(fileStream, Path.GetFileName(filePath), options, ct);
    }

    /// <summary>
    /// Describes the column layout parsed from the CSV header row.
    /// </summary>
    /// <param name="AnalogPairCount">Number of analog channel column pairs (ts + val).</param>
    /// <param name="HasDigitalPair">Whether the last column pair is a digital I/O pair (dio_ts, dio_val).</param>
    private sealed record CsvColumnLayout(int AnalogPairCount, bool HasDigitalPair);

    /// <summary>
    /// Parses device metadata and column layout from the comment header lines.
    /// </summary>
    private static (SdCardDeviceConfiguration Config, CsvColumnLayout Layout) ParseHeader(
        List<string> lines,
        SdCardParseOptions options)
    {
        string? deviceName = null;
        string? serialNumber = null;
        var timestampFreq = options.FallbackTimestampFrequency;
        var analogChannelCount = 0;
        var digitalChannelCount = 0;
        var hasDigitalPair = false;

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
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
            }
            else if (line.Contains("_ts,", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("ain", StringComparison.OrdinalIgnoreCase))
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

                break;
            }
            else
            {
                // First data row reached without finding column header
                break;
            }
        }

        var config = new SdCardDeviceConfiguration(
            AnalogPortCount: analogChannelCount,
            DigitalPortCount: digitalChannelCount,
            TimestampFrequency: timestampFreq,
            DeviceSerialNumber: serialNumber,
            DevicePartNumber: deviceName,
            FirmwareRevision: null,
            CalibrationValues: null);

        var layout = new CsvColumnLayout(analogChannelCount, hasDigitalPair);

        return (config, layout);
    }

    /// <summary>
    /// Finds the index of the first data row (skipping comments and the column header).
    /// </summary>
    private static int FindDataStartIndex(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith('#'))
            {
                continue;  // Comment line
            }

            // Check if it's the column header (contains "_ts," or starts with "ch"/"ain")
            if (line.Contains("_ts,", StringComparison.OrdinalIgnoreCase) ||
                (line.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && !char.IsDigit(line[2])) ||
                line.StartsWith("ain", StringComparison.OrdinalIgnoreCase))
            {
                continue;  // Column header line
            }

            return i;  // First data row
        }

        return lines.Count;  // No data found
    }

    private static async IAsyncEnumerable<SdCardLogEntry> ParseCsvLines(
        List<string> lines,
        int dataStartIndex,
        SdCardDeviceConfiguration config,
        CsvColumnLayout columnLayout,
        DateTime? fileCreatedDate,
        SdCardParseOptions options)
    {
        var baseTime = fileCreatedDate ?? DateTime.UtcNow;
        var timestampFreq = config.TimestampFrequency;
        var tickPeriod = timestampFreq > 0 ? 1.0 / timestampFreq : 0.0;

        uint? previousTimestamp = null;
        var elapsedSeconds = 0.0;
        var linesProcessed = 0;
        var bytesRead = 0L;

        var progress = options.Progress;
        var totalBytes = lines.Sum(l => l.Length + 1); // +1 for newline

        for (var i = dataStartIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            linesProcessed++;
            bytesRead += line.Length + 1;

            var parsed = TryParseCsvDataRow(line, columnLayout);
            if (parsed == null)
            {
                // Skip malformed lines
                continue;
            }

            var (rowTimestamp, rawAnalogValues, digitalData, perChannelTimestamps) = parsed.Value;

            // Scale raw ADC values using device calibration
            var analogValues = ScaleRawAnalogValues(rawAnalogValues, config);

            // Reconstruct absolute timestamp using first channel timestamp
            var absoluteTime = baseTime;
            if (tickPeriod > 0)
            {
                if (previousTimestamp == null)
                {
                    previousTimestamp = rowTimestamp;
                }
                else
                {
                    var delta = ComputeTickDelta(previousTimestamp.Value, rowTimestamp);
                    elapsedSeconds += delta * tickPeriod;
                    previousTimestamp = rowTimestamp;
                }

                absoluteTime = baseTime.AddSeconds(elapsedSeconds);
            }

            yield return new SdCardLogEntry(absoluteTime, analogValues, digitalData, perChannelTimestamps);

            // Report progress every 100 lines for efficiency
            if (linesProcessed % 100 == 0 && progress != null)
            {
                progress.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed));
            }
        }

        // Final progress report
        progress?.Report(new SdCardParseProgress(bytesRead, totalBytes, linesProcessed));

        await Task.CompletedTask; // keep the method async-compatible
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

    /// <summary>
    /// Scales raw ADC values to real voltage using device calibration data.
    /// Formula: (raw / resolution * portRange * calM + calB) * internalScaleM
    /// </summary>
    private static IReadOnlyList<double> ScaleRawAnalogValues(
        IReadOnlyList<double> rawValues,
        SdCardDeviceConfiguration? config)
    {
        if (config == null || config.Resolution == 0)
        {
            // No config or resolution available — return raw values as-is
            return rawValues;
        }

        var result = new double[rawValues.Count];
        var resolution = (double)config.Resolution;
        var cal = config.CalibrationValues;
        var portRange = config.PortRange;
        var intScale = config.InternalScaleM;

        for (var ch = 0; ch < rawValues.Count; ch++)
        {
            var calM = cal != null && ch < cal.Count ? cal[ch].Slope : 1.0;
            var calB = cal != null && ch < cal.Count ? cal[ch].Intercept : 0.0;
            var range = portRange != null && ch < portRange.Count ? portRange[ch] : 1.0;
            var scaleM = intScale != null && ch < intScale.Count ? intScale[ch] : 1.0;

            var normalized = rawValues[ch] / resolution;
            result[ch] = (normalized * range * calM + calB) * scaleM;
        }

        return result;
    }

    /// <summary>
    /// Merges an override configuration into a parsed configuration.
    /// File-parsed values are primary; the override fills in gaps (zero or null fields).
    /// </summary>
    private static SdCardDeviceConfiguration MergeConfiguration(
        SdCardDeviceConfiguration parsed,
        SdCardDeviceConfiguration? overrideConfig)
    {
        if (overrideConfig == null)
        {
            return parsed;
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: parsed.AnalogPortCount > 0 ? parsed.AnalogPortCount : overrideConfig.AnalogPortCount,
            DigitalPortCount: parsed.DigitalPortCount > 0 ? parsed.DigitalPortCount : overrideConfig.DigitalPortCount,
            TimestampFrequency: parsed.TimestampFrequency > 0 ? parsed.TimestampFrequency : overrideConfig.TimestampFrequency,
            DeviceSerialNumber: parsed.DeviceSerialNumber ?? overrideConfig.DeviceSerialNumber,
            DevicePartNumber: parsed.DevicePartNumber ?? overrideConfig.DevicePartNumber,
            FirmwareRevision: parsed.FirmwareRevision ?? overrideConfig.FirmwareRevision,
            CalibrationValues: parsed.CalibrationValues ?? overrideConfig.CalibrationValues,
            Resolution: parsed.Resolution > 0 ? parsed.Resolution : overrideConfig.Resolution,
            PortRange: parsed.PortRange ?? overrideConfig.PortRange,
            InternalScaleM: parsed.InternalScaleM ?? overrideConfig.InternalScaleM);
    }

    /// <summary>
    /// Computes the tick delta between two uint32 timestamps, handling rollover.
    /// </summary>
    private static long ComputeTickDelta(uint previous, uint current)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        // Rollover: ticks remaining to max + current
        return (long)(uint.MaxValue - previous) + current + 1;
    }

    private static async IAsyncEnumerable<SdCardLogEntry> EmptySamples()
    {
        await Task.CompletedTask;
        yield break;
    }
}
