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
/// The older one-line shape, <c>{"ts":...,"analog":[...],"digital":"hex"}</c>, still parses,
/// whatever order its fields come in.
/// </para>
/// <para>
/// One rule covers every shape, rather than a case for each: <b>every reading an object states
/// is a sample</b>. An <c>"ai"</c> array is a sample; so is each entry of a <c>"di"</c> array,
/// which is a reading at a time of its own and needs no analog reading beside it. Field order
/// matters only where the firmware's repeated <c>"ts"</c>/<c>"ai"</c> keys make order the one
/// thing that pairs a reading with its time, so a legacy object parses in any order. And a
/// damaged object costs one object, not the rest of the file
/// (<see cref="SdCardJsonObjectReader"/>).
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

            // A digital-only sample states no analog layout, so keep reading until a reading
            // does. A log with no analog readings at all is read through here, which costs one
            // pass over a file that holds nothing else to find.
            foreach (var sample in parsed.Samples)
            {
                if (sample.AnalogValues.Count > 0)
                {
                    analogChannelCount = sample.AnalogValues.Count;
                    break;
                }
            }

            if (analogChannelCount > 0)
            {
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

    /// <summary>One digital reading: the state a port was left in, and when.</summary>
    /// <param name="Timestamp">The raw device tick the reading was taken on.</param>
    /// <param name="State">The port state the entry reports.</param>
    private readonly record struct JsonDigitalReading(uint Timestamp, uint State);

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
    /// <para>
    /// The properties are walked in the order the file states them, because the firmware packs
    /// several samples into one object as repeated <c>"ts"</c> and <c>"ai"</c> keys and only
    /// their order says which reading belongs to which timestamp. Order is the tie-breaker and
    /// not the rule, though: a field only ends the sample before it when it would overwrite a
    /// slot that sample has already filled, so a legacy object states its fields in any order
    /// and still reads as one sample.
    /// </para>
    /// <para>
    /// Every reading becomes a sample. An <c>"ai"</c> array is one; so is each entry of a
    /// <c>"di"</c> array, which the firmware times as <c>StreamTrigStamp - sampleTime</c>
    /// (<c>JSON_Encoder.c</c>) and which therefore has a tick of its own — a digital reading on
    /// an analog sample's tick is carried by that sample, and one on any other tick is a sample
    /// with no analog values, as the binary parser's digital-only messages already are. A
    /// trigger stamp with no reading against it is still not a sample.
    /// </para>
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
            var analogReadings = new List<(uint Timestamp, IReadOnlyList<double> Values)>();
            var digitalReadings = new List<JsonDigitalReading>();

            // The legacy "digital" hex string belongs to its own object: that shape puts one
            // self-contained sample on each line, so an object that states no digital field
            // states a state of zero rather than inheriting one. Only the firmware's "di"
            // reports carry forward, in digitalState.
            var baselineDigital = digitalState;

            // The first timestamp the object states, which every tick in it is ordered against.
            uint? reference = null;

            // The timestamp a "di" array's offsets are stated against: the trigger stamp the
            // firmware writes ahead of the array.
            uint? trigger = null;

            uint? pendingTimestamp = null;
            IReadOnlyList<double>? pendingAnalog = null;

            void CloseReading(bool keepTimestamp)
            {
                if (pendingTimestamp != null && pendingAnalog != null)
                {
                    analogReadings.Add((pendingTimestamp.Value, pendingAnalog));
                }

                pendingAnalog = null;
                if (!keepTimestamp)
                {
                    pendingTimestamp = null;
                }
            }

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "meta":
                        meta = ReadMeta(property.Value);
                        break;

                    case "ts":
                        // Only a second timestamp ends the sample before it. A first one, after
                        // the readings it belongs to, simply names their time.
                        if (pendingTimestamp != null)
                        {
                            CloseReading(keepTimestamp: false);
                        }

                        if (property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.TryGetUInt32(out var timestamp))
                        {
                            pendingTimestamp = timestamp;
                            trigger = timestamp;
                            reference ??= timestamp;
                        }
                        else
                        {
                            pendingTimestamp = null;
                        }

                        break;

                    case "ai":
                        if (pendingAnalog != null)
                        {
                            CloseReading(keepTimestamp: true);
                        }

                        pendingAnalog = TryReadAnalogObjects(property.Value);
                        break;

                    case "analog":
                        if (pendingAnalog != null)
                        {
                            CloseReading(keepTimestamp: true);
                        }

                        pendingAnalog = TryReadAnalogNumbers(property.Value);
                        break;

                    case "di":
                        ReadDigitalReadings(
                            property.Value,
                            trigger,
                            digitalReadings,
                            ref digitalState,
                            ref baselineDigital);
                        break;

                    case "digital":
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var hexString = property.Value.GetString();
                            baselineDigital = string.IsNullOrEmpty(hexString)
                                ? 0u
                                : ParseDigitalHexString(hexString);
                        }

                        break;
                }
            }

            CloseReading(keepTimestamp: false);

            return new JsonObjectContent(
                meta,
                BuildSamples(analogReadings, digitalReadings, reference, baselineDigital),
                digitalState);
        }
    }

    /// <summary>
    /// Turns one object's readings into its samples, in the order they were taken.
    /// </summary>
    /// <param name="analogReadings">The analog readings and the ticks they state.</param>
    /// <param name="digitalReadings">The digital readings and the ticks they state.</param>
    /// <param name="reference">
    /// The first tick the object states, which the others are ordered against as signed offsets
    /// from it, so an object the 32-bit tick counter wraps inside still orders correctly.
    /// </param>
    /// <param name="baselineDigital">
    /// The digital state in force before the object's own digital readings — carried from the
    /// objects before it, or stated by a legacy <c>"digital"</c> field.
    /// </param>
    private static IReadOnlyList<JsonSample> BuildSamples(
        List<(uint Timestamp, IReadOnlyList<double> Values)> analogReadings,
        List<JsonDigitalReading> digitalReadings,
        uint? reference,
        uint baselineDigital)
    {
        if (analogReadings.Count == 0 && digitalReadings.Count == 0)
        {
            return Array.Empty<JsonSample>();
        }

        var anchor = reference ?? 0u;
        int Order(uint tick) => unchecked((int)(tick - anchor));

        // The firmware pops its digital queue from the front, so the entries arrive oldest
        // first; sorted anyway, so that a state is never read out of order. Two entries on one
        // tick keep the order the file states them in, so the later one is the state it leaves.
        var digitalOrder = new int[digitalReadings.Count];
        for (var i = 0; i < digitalReadings.Count; i++)
        {
            digitalOrder[i] = i;
        }

        Array.Sort(digitalOrder, (left, right) =>
        {
            var byTick = Order(digitalReadings[left].Timestamp)
                .CompareTo(Order(digitalReadings[right].Timestamp));
            return byTick != 0 ? byTick : left.CompareTo(right);
        });

        var sortedDigital = new JsonDigitalReading[digitalReadings.Count];
        for (var i = 0; i < digitalOrder.Length; i++)
        {
            sortedDigital[i] = digitalReadings[digitalOrder[i]];
        }

        var samples = new List<(int Order, int Index, JsonSample Sample)>(
            analogReadings.Count + sortedDigital.Length);

        foreach (var (tick, values) in analogReadings)
        {
            var order = Order(tick);

            // The state in force when this reading was taken, not the state the object ends in.
            var state = baselineDigital;
            foreach (var digital in sortedDigital)
            {
                if (Order(digital.Timestamp) > order)
                {
                    break;
                }

                state = digital.State;
            }

            samples.Add((order, samples.Count, new JsonSample(tick, values, state)));
        }

        foreach (var digital in sortedDigital)
        {
            var order = Order(digital.Timestamp);

            // A digital reading on an analog sample's tick is already carried by that sample.
            var carried = false;
            foreach (var (tick, _) in analogReadings)
            {
                if (Order(tick) == order)
                {
                    carried = true;
                    break;
                }
            }

            if (!carried)
            {
                samples.Add((
                    order,
                    samples.Count,
                    new JsonSample(digital.Timestamp, Array.Empty<double>(), digital.State)));
            }
        }

        samples.Sort((left, right) =>
        {
            var byTick = left.Order.CompareTo(right.Order);
            return byTick != 0 ? byTick : left.Index.CompareTo(right.Index);
        });

        var ordered = new JsonSample[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            ordered[i] = samples[i].Sample;
        }

        return ordered;
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
    /// Reads the firmware's <c>"di":[{"ts":..,"mask":..,"val":..},...]</c> array.
    /// </summary>
    /// <param name="element">The <c>"di"</c> value.</param>
    /// <param name="trigger">
    /// The trigger stamp the entries' offsets are stated against, or <c>null</c> when the object
    /// has stated no timestamp yet.
    /// </param>
    /// <param name="readings">Collects each entry's state and the tick it was taken on.</param>
    /// <param name="carriedState">
    /// The state to carry into the objects that follow. A digital line holds its level until the
    /// next report, so the last state reported carries forward.
    /// </param>
    /// <param name="baselineState">
    /// The state a sample takes when no reading in this object precedes it.
    /// </param>
    /// <remarks>
    /// The firmware writes each entry's time as <c>StreamTrigStamp - sampleTime</c>
    /// (<c>JSON_Encoder.c</c>), so the entry was taken on tick <c>trigger - ts</c>, counted the
    /// way the device's own 32-bit counter counts. An entry read before the object has stated
    /// any timestamp names no tick, so it sets the state without becoming a sample.
    /// </remarks>
    private static void ReadDigitalReadings(
        JsonElement element,
        uint? trigger,
        List<JsonDigitalReading> readings,
        ref uint carriedState,
        ref uint baselineState)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("val", out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetUInt32(out var state))
            {
                continue;
            }

            carriedState = state;

            if (trigger == null)
            {
                baselineState = state;
                continue;
            }

            var offset = item.TryGetProperty("ts", out var ts)
                         && ts.ValueKind == JsonValueKind.Number
                         && ts.TryGetUInt32(out var stated)
                ? stated
                : 0u;

            readings.Add(new JsonDigitalReading(unchecked(trigger.Value - offset), state));
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
}
