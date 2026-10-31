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
/// Parses SD card log <c>.json</c> files produced by DAQiFi firmware.
/// <para>
/// The firmware writes the log as a stream of JSON objects rather than one object per line
/// (<c>JSON_Encoder.c</c>):
/// </para>
/// <list type="bullet">
///   <item><description>A one-line metadata header, <c>{"meta":{"dev":...,"sn":...,"tick_hz":...}}</c>,
///   written once at the start of a session.</description></item>
///   <item><description>Sample objects that open with a bare <c>{</c> and spend one line on each
///   field: <c>"ts"</c>, then <c>"ai":[{"ch":0,"val":...},...]</c> with one entry per enabled
///   channel, plus an optional <c>"di"</c> array of digital samples.</description></item>
///   <item><description>Several samples queued at one encoder call go into a single object as
///   repeated <c>"ts"</c> and <c>"ai"</c> keys.</description></item>
/// </list>
/// <para>
/// The older one-line shape, <c>{"ts":...,"analog":[...],"digital":"hex"}</c>, still parses.
/// </para>
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SdCardParseOptions.BufferSize"/> is not greater than zero.
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
    /// Parses an SD card JSON log file from a file path.
    /// </summary>
    /// <param name="filePath">The path to the JSON log file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SdCardParseOptions.BufferSize"/> is not greater than zero.
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
    /// Reads the metadata header and the first sample-bearing object to settle the layout, then
    /// hands the session a sample iterator that re-reads the file lazily rather than holding its
    /// objects in memory.
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

        if (!header.SawAnyObject)
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

        var samples = ParseJsonObjects(
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

    /// <summary>
    /// What the preamble states: whether the file holds anything at all, and the device context
    /// the file itself supplies.
    /// </summary>
    /// <param name="SawAnyObject">Whether the file contained any complete JSON object.</param>
    /// <param name="Config">Device metadata and channel layout stated by the file itself.</param>
    private sealed record JsonHeader(bool SawAnyObject, SdCardDeviceConfiguration Config);

    /// <summary>
    /// Reads the metadata header and the first object that carries samples, and stops there.
    /// Nothing past the first sample is read, so a session still produces its first sample
    /// without reading the whole log.
    /// </summary>
    private static async Task<JsonHeader> ReadHeaderAsync(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        CancellationToken ct)
    {
        string? deviceName = null;
        string? serialNumber = null;

        // File-stated frequency only. The device override and the caller's fallback are applied
        // afterwards, in that order, so that a connected device's real clock beats a fallback
        // guess instead of losing to it.
        var timestampFrequency = 0u;
        var analogChannelCount = 0;
        var sawAnyObject = false;

        await foreach (var jsonObject in SdCardJsonObjectReader.ReadObjectsAsync(openLines, ct)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            sawAnyObject = true;

            var parsed = TryReadObject(jsonObject.Text, digitalState: 0u);
            if (parsed == null)
            {
                continue;
            }

            if (parsed.Meta != null)
            {
                deviceName = parsed.Meta.DeviceName;
                serialNumber = parsed.Meta.SerialNumber;
                timestampFrequency = parsed.Meta.TickHz;
            }

            if (parsed.Samples.Count > 0)
            {
                analogChannelCount = parsed.Samples[0].AnalogValues.Count;
                break;
            }
        }

        var config = new SdCardDeviceConfiguration(
            AnalogPortCount: analogChannelCount,
            DigitalPortCount: 0,  // The firmware's log states no digital port count.
            TimestampFrequency: timestampFrequency,
            DeviceSerialNumber: serialNumber,
            DevicePartNumber: deviceName,
            FirmwareRevision: null,
            CalibrationValues: null);

        return new JsonHeader(sawAnyObject, config);
    }

    private static async IAsyncEnumerable<SdCardLogEntry> ParseJsonObjects(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        Func<long> totalBytesProvider,
        SdCardDeviceConfiguration config,
        DateTime baseTime,
        IProgress<SdCardParseProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestampReconstructor =
            SdCardTimestampReconstructor.ForTimestampFrequency(config.TimestampFrequency);

        var samplesProduced = 0;
        var bytesRead = 0L;
        var totalBytes = totalBytesProvider();

        // The firmware writes "di" only when digital samples are queued, so an object can carry
        // analog samples and no digital report at all. A digital line holds its level until the
        // next report, so the last reported state carries forward.
        var digitalState = 0u;

        await foreach (var jsonObject in SdCardJsonObjectReader.ReadObjectsAsync(openLines, ct)
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            bytesRead = jsonObject.BytesRead;

            var parsed = TryReadObject(jsonObject.Text, digitalState);
            if (parsed == null)
            {
                // Skip malformed objects
                continue;
            }

            digitalState = parsed.DigitalState;

            foreach (var sample in parsed.Samples)
            {
                // Scale raw ADC values using device calibration
                var analogValues = SdCardAnalogScaling.ScaleRawAnalogValues(sample.AnalogValues, config);

                // Reconstruct absolute timestamp
                var hasDeviceTimestamp = timestampReconstructor.HasDeviceClock;
                var absoluteTime = hasDeviceTimestamp
                    ? timestampReconstructor.Advance(sample.Timestamp, baseTime)
                    : baseTime;

                yield return new SdCardLogEntry(absoluteTime, analogValues, sample.DigitalData, null)
                {
                    HasDeviceTimestamp = hasDeviceTimestamp
                };

                samplesProduced++;

                // Report progress every 100 samples for efficiency
                if (samplesProduced % 100 == 0 && progress != null)
                {
                    progress.Report(new SdCardParseProgress(bytesRead, totalBytes, samplesProduced));
                }
            }
        }

        // Final progress report
        progress?.Report(new SdCardParseProgress(bytesRead, totalBytes, samplesProduced));
    }

    /// <summary>The device context a <c>"meta"</c> header states.</summary>
    /// <param name="DeviceName">The <c>"dev"</c> value, for example <c>Nyquist 1</c>.</param>
    /// <param name="SerialNumber">
    /// The <c>"sn"</c> value, kept exactly as written. The firmware formats it as 16 hex digits
    /// in both the JSON and the CSV log, and the CSV parser reports it the same way.
    /// </param>
    /// <param name="TickHz">The <c>"tick_hz"</c> value, or zero when the header states none.</param>
    private sealed record JsonMeta(string? DeviceName, string? SerialNumber, uint TickHz);

    /// <summary>One sample read out of a JSON object, before scaling.</summary>
    /// <param name="Timestamp">The raw device tick count.</param>
    /// <param name="AnalogValues">
    /// The channel readings, in the order the file states them. The firmware writes one entry
    /// per enabled channel, in ascending channel order, and names the channel in each entry's
    /// <c>"ch"</c> field; looking calibration up by that number instead of by position is
    /// tracked separately in daqifi-core#849, which covers the CSV path the same way.
    /// </param>
    /// <param name="DigitalData">The digital state in force when this sample was taken.</param>
    private sealed record JsonSample(uint Timestamp, IReadOnlyList<double> AnalogValues, uint DigitalData);

    /// <summary>Everything one JSON object contributed.</summary>
    /// <param name="Meta">The metadata header, when this object was one.</param>
    /// <param name="Samples">The samples the object states, in order.</param>
    /// <param name="DigitalState">The digital state to carry into the next object.</param>
    private sealed record JsonObjectContent(
        JsonMeta? Meta,
        IReadOnlyList<JsonSample> Samples,
        uint DigitalState);

    /// <summary>
    /// Reads one complete JSON object.
    /// </summary>
    /// <param name="objectText">The object text, as the object reader produced it.</param>
    /// <param name="digitalState">The digital state carried in from earlier objects.</param>
    /// <returns>What the object contributed, or <c>null</c> when it is not usable JSON.</returns>
    /// <remarks>
    /// The properties are walked in the order the file states them, because the firmware packs
    /// several samples into one object as repeated <c>"ts"</c> and <c>"ai"</c> keys and only
    /// their order says which reading belongs to which timestamp. A <c>"ts"</c> therefore ends
    /// the sample before it and starts a new one, and a sample is only reported once a reading
    /// has arrived for it — which is what drops the single trigger timestamp the firmware writes
    /// ahead of a <c>"di"</c> array.
    /// </remarks>
    private static JsonObjectContent? TryReadObject(string objectText, uint digitalState)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(objectText);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonMeta? meta = null;
            var samples = new List<JsonSample>();

            // The legacy "digital" hex string belongs to its own object: that shape puts one
            // self-contained sample on each line, so an object that states no digital field
            // states a state of zero rather than inheriting one. Only the firmware's "di"
            // reports carry forward, in digitalState.
            var sampleDigital = digitalState;

            uint? pendingTimestamp = null;
            IReadOnlyList<double>? pendingAnalog = null;

            void FlushPendingSample()
            {
                if (pendingTimestamp != null && pendingAnalog != null)
                {
                    samples.Add(new JsonSample(pendingTimestamp.Value, pendingAnalog, sampleDigital));
                }

                pendingTimestamp = null;
                pendingAnalog = null;
            }

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "meta":
                        meta = ReadMeta(property.Value);
                        break;

                    case "ts":
                        FlushPendingSample();
                        pendingTimestamp =
                            property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.TryGetUInt32(out var timestamp)
                                ? timestamp
                                : null;
                        break;

                    case "ai":
                        pendingAnalog = TryReadAnalogObjects(property.Value);
                        break;

                    case "analog":
                        pendingAnalog = TryReadAnalogNumbers(property.Value);
                        break;

                    case "di":
                        if (TryReadDigitalSamples(property.Value, out var reported))
                        {
                            digitalState = reported;
                            sampleDigital = reported;
                        }

                        break;

                    case "digital":
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var hexString = property.Value.GetString();
                            sampleDigital = string.IsNullOrEmpty(hexString)
                                ? 0u
                                : ParseDigitalHexString(hexString);
                        }

                        break;
                }
            }

            FlushPendingSample();

            return new JsonObjectContent(meta, samples, digitalState);
        }
    }

    private static JsonMeta ReadMeta(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new JsonMeta(null, null, 0u);
        }

        var deviceName = element.TryGetProperty("dev", out var dev) && dev.ValueKind == JsonValueKind.String
            ? dev.GetString()
            : null;

        var serialNumber = element.TryGetProperty("sn", out var sn) && sn.ValueKind == JsonValueKind.String
            ? sn.GetString()
            : null;

        var tickHz = element.TryGetProperty("tick_hz", out var tick)
                     && tick.ValueKind == JsonValueKind.Number
                     && tick.TryGetUInt32(out var hz)
            ? hz
            : 0u;

        return new JsonMeta(deviceName, serialNumber, tickHz);
    }

    /// <summary>
    /// Reads the firmware's <c>"ai":[{"ch":0,"val":...},...]</c> array.
    /// </summary>
    /// <returns>
    /// The values, in the order the array states them, or <c>null</c> when any entry is not a
    /// channel reading — in which case the sample is dropped rather than reported short.
    /// </returns>
    private static IReadOnlyList<double>? TryReadAnalogObjects(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<double>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("val", out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var reading))
            {
                return null;
            }

            values.Add(reading);
        }

        return values;
    }

    /// <summary>
    /// Reads the legacy <c>"analog":[1.0,2.0]</c> array.
    /// </summary>
    /// <returns>
    /// The values, or <c>null</c> when any element is not a number — in which case the sample is
    /// dropped, as it was before the firmware shape was supported.
    /// </returns>
    private static IReadOnlyList<double>? TryReadAnalogNumbers(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<double>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var reading))
            {
                return null;
            }

            values.Add(reading);
        }

        return values;
    }

    /// <summary>
    /// Reads the firmware's <c>"di":[{"ts":..,"mask":..,"val":..},...]</c> array and returns the
    /// digital state it leaves behind.
    /// </summary>
    /// <param name="element">The <c>"di"</c> value.</param>
    /// <param name="state">The last state the array reports.</param>
    /// <returns><c>true</c> when the array reported a state at all.</returns>
    /// <remarks>
    /// Each entry carries its own timestamp, relative to the object's trigger stamp. An
    /// <see cref="SdCardLogEntry"/> holds one digital word per analog sample and no place for a
    /// digital-only reading, so the last entry — the state the port is left in — is what the
    /// samples report.
    /// </remarks>
    private static bool TryReadDigitalSamples(JsonElement element, out uint state)
    {
        state = 0u;

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var reported = false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("val", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetUInt32(out var reading))
            {
                state = reading;
                reported = true;
            }
        }

        return reported;
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
}
