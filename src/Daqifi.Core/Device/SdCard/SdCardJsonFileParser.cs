using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        return await SdCardTextFileSource.ParseAsync(
            fileStream,
            ct,
            (openLines, totalBytes, token) => BuildSessionAsync(openLines, totalBytes, fileName, options, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an SD card JSON log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the JSON log file.</param>
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

        return await SdCardTextFileSource.ParseFileAsync(
            filePath,
            options.BufferSize,
            ct,
            (openLines, totalBytes, token) => BuildSessionAsync(openLines, totalBytes, Path.GetFileName(filePath), options, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the first line to infer the channel layout, then hands the session a sample
    /// iterator that re-reads the file lazily rather than holding its lines in memory.
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

        // Only the first line is needed to infer the layout — JSONL carries no separate header.
        string? firstLine = null;
        await foreach (var line in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            firstLine = line.Text;
            break;
        }

        if (firstLine == null)
        {
            // Empty file
            return new SdCardLogSession(
                fileName,
                fileCreatedDate,
                null,
                SdCardTextFileSource.EmptySamples());
        }

        var config = InferConfiguration(firstLine, options);

        // JSON log lines carry raw tick counts and no frequency of their own, so the file can
        // only ever contribute 0 here; a connected device's frequency is what stands between
        // the caller's fallback guess and the data.
        var (timestampFrequency, timestampSource) = SdCardTimestampFrequencyResolver.Resolve(
            fileFrequencyHz: 0u,
            options.ConfigurationOverride?.TimestampFrequency ?? 0u,
            options.FallbackTimestampFrequency);

        config = config with { TimestampFrequency = timestampFrequency };

        var samples = ParseJsonLines(
            openLines,
            totalBytes,
            config,
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

    private static async IAsyncEnumerable<SdCardLogEntry> ParseJsonLines(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        Func<long> totalBytesProvider,
        SdCardDeviceConfiguration config,
        DateTime baseTime,
        IProgress<SdCardParseProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestampFreq = config.TimestampFrequency;
        var tickPeriod = timestampFreq > 0 ? 1.0 / timestampFreq : 0.0;
        var timestampReconstructor = new SdCardTimestampReconstructor(tickPeriod);

        var linesProcessed = 0;
        var bytesRead = 0L;
        var totalBytes = totalBytesProvider();

        await foreach (var entry in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var line = entry.Text;
            linesProcessed++;
            bytesRead = entry.BytesRead;

            var parsed = TryParseJsonLine(line);
            if (parsed == null)
            {
                // Skip malformed lines
                continue;
            }

            var (timestamp, rawAnalogValues, digitalData) = parsed.Value;

            // Scale raw ADC values using device calibration
            var analogValues = SdCardAnalogScaling.ScaleRawAnalogValues(rawAnalogValues, config);

            // Reconstruct absolute timestamp
            var hasDeviceTimestamp = timestampReconstructor.HasDeviceClock;
            var absoluteTime = hasDeviceTimestamp
                ? timestampReconstructor.Advance(timestamp, baseTime)
                : baseTime;

            yield return new SdCardLogEntry(absoluteTime, analogValues, digitalData, null)
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
        // Parse first line to infer analog channel count
        var parsed = TryParseJsonLine(firstLine);
        var analogCount = parsed?.analog.Count ?? 0;

        var inferred = new SdCardDeviceConfiguration(
            AnalogPortCount: analogCount,
            DigitalPortCount: 0,  // Cannot infer from data
            // The frequency is resolved by the caller, after the override merge, so that a
            // connected device's real clock beats a fallback guess rather than losing to it.
            TimestampFrequency: 0u,
            DeviceSerialNumber: null,
            DevicePartNumber: null,
            FirmwareRevision: null,
            CalibrationValues: null);

        return SdCardConfigurationMerge.Merge(inferred, options.ConfigurationOverride);
    }

}
