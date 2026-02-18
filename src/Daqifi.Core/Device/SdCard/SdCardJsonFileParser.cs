using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Parses SD card log <c>.json</c> files containing JSONL-formatted sample data.
/// Each line is a JSON object: {"ts":timestamp,"analog":[...],"digital":"hex"}.
/// </summary>
public sealed class SdCardJsonFileParser
{
    /// <summary>
    /// Parses an SD card JSON log file from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="fileStream">A readable stream containing the JSON log data.</param>
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

        var config = InferConfiguration(lines[0], options);

        var samples = ParseJsonLines(
            lines,
            config,
            fileCreatedDate,
            options);

        return new SdCardLogSession(fileName, fileCreatedDate, config, samples);
    }

    /// <summary>
    /// Parses an SD card JSON log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the JSON log file.</param>
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

    private static async IAsyncEnumerable<SdCardLogEntry> ParseJsonLines(
        List<string> lines,
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
        var totalBytes = lines.Sum(l => l.Length + 1); // +1 for newline

        foreach (var line in lines)
        {
            linesProcessed++;
            bytesRead += line.Length + 1;

            var parsed = TryParseJsonLine(line);
            if (parsed == null)
            {
                // Skip malformed lines
                continue;
            }

            var (timestamp, analogValues, digitalData) = parsed.Value;

            // Reconstruct absolute timestamp
            var absoluteTime = baseTime;
            if (tickPeriod > 0)
            {
                if (previousTimestamp == null)
                {
                    previousTimestamp = timestamp;
                }
                else
                {
                    var delta = ComputeTickDelta(previousTimestamp.Value, timestamp);
                    elapsedSeconds += delta * tickPeriod;
                    previousTimestamp = timestamp;
                }

                absoluteTime = baseTime.AddSeconds(elapsedSeconds);
            }

            yield return new SdCardLogEntry(absoluteTime, analogValues, digitalData, null);

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

    private static (uint timestamp, IReadOnlyList<double> analog, uint digital)? TryParseJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Parse timestamp
            if (!root.TryGetProperty("ts", out var tsElement) || !tsElement.TryGetUInt32(out var timestamp))
            {
                return null;
            }

            // Parse analog array
            if (!root.TryGetProperty("analog", out var analogElement) || analogElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var analogList = new List<double>();
            foreach (var item in analogElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                {
                    analogList.Add(item.GetDouble());
                }
                else
                {
                    return null;
                }
            }

            // Parse digital hex string
            var digitalData = 0u;
            if (root.TryGetProperty("digital", out var digitalElement) && digitalElement.ValueKind == JsonValueKind.String)
            {
                var hexString = digitalElement.GetString();
                if (!string.IsNullOrEmpty(hexString))
                {
                    digitalData = ParseDigitalHexString(hexString);
                }
            }

            return (timestamp, analogList, digitalData);
        }
        catch
        {
            return null;
        }
    }

    private static uint ParseDigitalHexString(string hexString)
    {
        if (string.IsNullOrEmpty(hexString))
        {
            return 0;
        }

        var hexBytes = hexString.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var result = 0u;

        for (var i = 0; i < hexBytes.Length && i < 4; i++)  // Max 4 bytes for uint32
        {
            if (byte.TryParse(hexBytes[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var byteValue))
            {
                result |= (uint)byteValue << (i * 8);  // Little-endian packing
            }
        }

        return result;
    }

    private static SdCardDeviceConfiguration InferConfiguration(string firstLine, SdCardParseOptions options)
    {
        // Use override if provided
        if (options.ConfigurationOverride != null)
        {
            return options.ConfigurationOverride;
        }

        // Parse first line to infer analog channel count
        var parsed = TryParseJsonLine(firstLine);
        var analogCount = parsed?.analog.Count ?? 0;

        var timestampFreq = options.FallbackTimestampFrequency;
        if (timestampFreq == 0)
        {
            timestampFreq = 50_000_000;  // Default for Nyquist devices
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: analogCount,
            DigitalPortCount: 0,  // Cannot infer from data
            TimestampFrequency: timestampFreq,
            DeviceSerialNumber: null,
            DevicePartNumber: null,
            FirmwareRevision: null,
            CalibrationValues: null);
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
