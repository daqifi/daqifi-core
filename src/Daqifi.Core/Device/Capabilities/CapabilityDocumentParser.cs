using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// Parses the responses to <c>CONFigure:CAPabilities:JSON?</c> and
/// <c>CONFigure:CAPabilities:APIVersion?</c>.
/// </summary>
/// <remarks>
/// <para>
/// Tolerant by design. Every parse method returns <c>false</c> instead of throwing, and every
/// optional field is read through a helper that yields <c>null</c> when the field is absent or has
/// an unexpected type. That is what lets an unfamiliar firmware revision degrade to the
/// board-derived capability table (ADR 0001) rather than fail a device's initialization: the
/// firmware's schema rules make additive change routine, and unknown fields must be ignored.
/// </para>
/// </remarks>
public static class CapabilityDocumentParser
{
    /// <summary>
    /// Attempts to parse a capability document from a single JSON string.
    /// </summary>
    /// <param name="json">The JSON document as emitted by <c>CONFigure:CAPabilities:JSON?</c>.</param>
    /// <param name="document">The parsed document, or <c>null</c> when parsing failed.</param>
    /// <returns><c>true</c> when a capability document was parsed.</returns>
    public static bool TryParse(string? json, [NotNullWhen(true)] out CapabilityDocument? document)
    {
        document = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Require the schema version. It is the one field the firmware always emits, so
            // demanding it distinguishes a capability document from any other JSON that might
            // arrive on the same text channel — and a rename would itself be a breaking change.
            var schemaVersion = ReadInt(root, "schema_version");
            if (!schemaVersion.HasValue)
            {
                return false;
            }

            var hasStorage = root.TryGetProperty("storage", out var storage)
                             && storage.ValueKind == JsonValueKind.Object;
            var hasPower = root.TryGetProperty("power", out var power)
                           && power.ValueKind == JsonValueKind.Object;

            document = new CapabilityDocument
            {
                SchemaVersion = schemaVersion.Value,
                SchemaUri = ReadString(root, "schema_uri"),
                Identity = ReadIdentity(root),
                Channels = ReadChannels(root),
                Streaming = ReadStreaming(root),
                SdSupported = hasStorage ? ReadBool(storage, "sd_supported") : null,
                UsbSupported = ReadTransportSupported(root, "usb"),
                WifiSupported = ReadTransportSupported(root, "wifi"),
                EthernetSupported = ReadTransportSupported(root, "ethernet"),
                BatteryPresent = hasPower ? ReadBool(power, "battery_present") : null,
                ExternalPowerSupported = hasPower ? ReadBool(power, "external_power_supported") : null,
                RawJson = json
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse a capability document from a device's text response lines, trying each
    /// line that looks like a JSON object until one parses.
    /// </summary>
    /// <remarks>
    /// The response can also contain the command echo and the device prompt, so lines are filtered
    /// rather than assumed to be the document.
    /// </remarks>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="document">The parsed document, or <c>null</c> when no line parsed.</param>
    /// <returns><c>true</c> when a capability document was parsed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <c>null</c>.</exception>
    public static bool TryParseLines(
        IEnumerable<string> lines,
        [NotNullWhen(true)] out CapabilityDocument? document)
    {
        ArgumentNullException.ThrowIfNull(lines);

        document = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            if (TryParse(trimmed, out document))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to read the capability schema version from the response lines of
    /// <c>CONFigure:CAPabilities:APIVersion?</c>.
    /// </summary>
    /// <remarks>
    /// The response is a bare integer, but it can be accompanied by the command echo, the device
    /// prompt, or a SCPI error line on firmware that does not implement the query — so the first
    /// line that is <i>entirely</i> an integer wins, and error lines are skipped explicitly.
    /// </remarks>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="apiVersion">The reported schema version, or <c>0</c> when none was found.</param>
    /// <returns><c>true</c> when a version was read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <c>null</c>.</exception>
    public static bool TryParseApiVersion(IEnumerable<string> lines, out int apiVersion)
    {
        ArgumentNullException.ThrowIfNull(lines);

        apiVersion = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || ScpiResponseClassifier.IsErrorResponseLine(line))
            {
                continue;
            }

            if (int.TryParse(
                    line.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                apiVersion = value;
                return true;
            }
        }

        return false;
    }

    private static CapabilityIdentity? ReadIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("identity", out var identity) || identity.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CapabilityIdentity
        {
            Vendor = ReadString(identity, "vendor"),
            Model = ReadString(identity, "model"),
            Variant = ReadString(identity, "variant"),
            Serial = ReadString(identity, "serial"),
            FirmwareRevision = ReadString(identity, "firmware_rev"),
            HardwareRevision = ReadString(identity, "hardware_rev")
        };
    }

    private static IReadOnlyList<CapabilityChannel> ReadChannels(JsonElement root)
    {
        if (!root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CapabilityChannel>();
        }

        var result = new List<CapabilityChannel>(channels.GetArrayLength());
        foreach (var element in channels.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadInt(element, "id");
            var rawKind = ReadString(element, "kind");
            if (!id.HasValue || rawKind == null)
            {
                // An entry with no identity is unusable; skip it rather than inventing an index,
                // which would corrupt the per-kind counts the merge relies on.
                continue;
            }

            var range = ReadFirstRange(element);
            var (supportsPwm, pwmMin, pwmMax) = ReadPwm(element);
            var (slope, intercept) = ReadCalibration(element);

            result.Add(new CapabilityChannel
            {
                Id = id.Value,
                Kind = ParseKind(rawKind),
                RawKind = rawKind,
                SignalType = ReadString(element, "signal_type"),
                Unit = ReadString(element, "unit"),
                ResolutionBits = ReadInt(element, "resolution_bits"),
                IsSimultaneous = ReadBool(element, "simultaneous") ?? false,
                IsDifferential = ReadBool(element, "differential") ?? false,
                RangeMinimum = range.Minimum,
                RangeMaximum = range.Maximum,
                SupportsPwm = supportsPwm,
                PwmMinimumFrequencyHz = pwmMin,
                PwmMaximumFrequencyHz = pwmMax,
                CalibrationSlope = slope,
                CalibrationIntercept = intercept
            });
        }

        return result;
    }

    private static CapabilityChannelKind ParseKind(string rawKind) => rawKind switch
    {
        "analog-input" => CapabilityChannelKind.AnalogInput,
        "analog-output" => CapabilityChannelKind.AnalogOutput,
        "digital-io" => CapabilityChannelKind.DigitalIo,
        _ => CapabilityChannelKind.Unknown
    };

    private static (double? Minimum, double? Maximum) ReadFirstRange(JsonElement channel)
    {
        if (!channel.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var range in ranges.EnumerateArray())
        {
            if (range.ValueKind == JsonValueKind.Object)
            {
                return (ReadDouble(range, "min"), ReadDouble(range, "max"));
            }
        }

        return (null, null);
    }

    private static (bool SupportsPwm, int? MinimumHz, int? MaximumHz) ReadPwm(JsonElement channel)
    {
        // The schema signals digital-pin features by key presence: a pin without PWM simply omits
        // the "pwm" object, so absence is the negative answer rather than a false-valued flag.
        if (!channel.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Object
            || !features.TryGetProperty("pwm", out var pwm)
            || pwm.ValueKind != JsonValueKind.Object)
        {
            return (false, null, null);
        }

        return (true, ReadInt(pwm, "min_freq_hz"), ReadInt(pwm, "max_freq_hz"));
    }

    private static (double? Slope, double? Intercept) ReadCalibration(JsonElement channel)
    {
        if (!channel.TryGetProperty("calibration", out var calibration)
            || calibration.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadDouble(calibration, "slope"), ReadDouble(calibration, "intercept"));
    }

    private static CapabilityStreaming? ReadStreaming(JsonElement root)
    {
        if (!root.TryGetProperty("streaming", out var streaming) || streaming.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? minimumRate = null;
        int? maximumRate = null;
        if (streaming.TryGetProperty("sample_rate_range_hz", out var range)
            && range.ValueKind == JsonValueKind.Object)
        {
            minimumRate = ReadInt(range, "min");
            maximumRate = ReadInt(range, "max");
        }

        return new CapabilityStreaming
        {
            MinimumSampleRateHz = minimumRate,
            MaximumSampleRateHz = maximumRate,
            ConservativeEnvelopeHz = ReadInt(streaming, "conservative_envelope_hz"),
            CurrentMaximumRateHz = ReadInt(streaming, "current_max_rate_hz"),
            RateValidation = ReadString(streaming, "rate_validation"),
            RateModel = ReadRateModel(streaming),
            Encodings = ReadStringArray(streaming, "encodings"),
            Transports = ReadStringArray(streaming, "transports")
        };
    }

    private static CapabilityRateModel? ReadRateModel(JsonElement streaming)
    {
        if (!streaming.TryGetProperty("rate_model", out var model) || model.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CapabilityRateModel
        {
            Formula = ReadString(model, "formula"),
            AbsoluteMaximumHz = ReadInt(model, "absolute_max_hz"),
            Type1AggregateMaximumHz = ReadInt(model, "type1_aggregate_max_hz"),
            PerTickBudgetHz = ReadInt(model, "per_tick_budget_hz"),
            PerTickOverhead = ReadInt(model, "per_tick_overhead")
        };
    }

    private static bool? ReadTransportSupported(JsonElement root, string transportName)
    {
        if (!root.TryGetProperty("transports", out var transports)
            || transports.ValueKind != JsonValueKind.Object
            || !transports.TryGetProperty(transportName, out var transport)
            || transport.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadBool(transport, "supported");
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsedValue)
            ? parsedValue
            : null;

    private static double? ReadDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsedValue)
            ? parsedValue
            : null;

    private static bool? ReadBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (value != null)
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }
}
