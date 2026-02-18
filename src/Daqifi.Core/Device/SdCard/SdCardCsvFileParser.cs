using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
///   <item><description>A column header row: <c>ch0_ts,ch0_val,ch1_ts,ch1_val,...</c></description></item>
///   <item><description>Data rows with interleaved per-channel timestamp/value pairs.</description></item>
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

        var config = options.ConfigurationOverride ?? ParseHeader(lines, options);

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
    /// Parses device metadata from the comment header lines.
    /// </summary>
    private static SdCardDeviceConfiguration ParseHeader(List<string> lines, SdCardParseOptions options)
    {
        string? deviceName = null;
        string? serialNumber = null;
        var timestampFreq = options.FallbackTimestampFrequency;
        var analogChannelCount = 0;

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
                     line.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
            {
                // Column header: ch0_ts,ch0_val,ch1_ts,ch1_val,...
                // Count channel pairs (every 2 columns = 1 channel)
                var cols = line.Split(',');
                analogChannelCount = cols.Length / 2;
                break;
            }
            else
            {
                // First data row reached without finding column header
                break;
            }
        }

        if (timestampFreq == 0)
        {
            timestampFreq = 50_000_000;  // Default for Nyquist devices
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: analogChannelCount,
            DigitalPortCount: 0,
            TimestampFrequency: timestampFreq,
            DeviceSerialNumber: serialNumber,
            DevicePartNumber: deviceName,
            FirmwareRevision: null,
            CalibrationValues: null);
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

            // Check if it's the column header (contains "_ts," or starts with "ch")
            if (line.Contains("_ts,", StringComparison.OrdinalIgnoreCase) ||
                (line.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && !char.IsDigit(line[2])))
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
        var dataLines = lines.Count - dataStartIndex;
        var totalBytes = lines.Sum(l => l.Length + 1); // +1 for newline

        for (var i = dataStartIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            linesProcessed++;
            bytesRead += line.Length + 1;

            var parsed = TryParseCsvDataRow(line);
            if (parsed == null)
            {
                // Skip malformed lines
                continue;
            }

            var (rowTimestamp, analogValues, perChannelTimestamps) = parsed.Value;

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

            yield return new SdCardLogEntry(absoluteTime, analogValues, 0u, perChannelTimestamps);

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
    /// Format: ch0_ts,ch0_val,ch1_ts,ch1_val,...
    /// </summary>
    private static (uint rowTimestamp, IReadOnlyList<double> analogValues, IReadOnlyList<uint> perChannelTimestamps)? TryParseCsvDataRow(string line)
    {
        try
        {
            var columns = line.Split(',');
            // Must have at least one channel pair (2 columns: ts + val)
            if (columns.Length < 2 || columns.Length % 2 != 0)
            {
                return null;
            }

            var channelCount = columns.Length / 2;
            var analogValues = new List<double>(channelCount);
            var perChannelTimestamps = new List<uint>(channelCount);

            for (var ch = 0; ch < channelCount; ch++)
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

            // Use first channel's timestamp as the row timestamp
            return (perChannelTimestamps[0], analogValues, perChannelTimestamps);
        }
        catch
        {
            return null;
        }
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
