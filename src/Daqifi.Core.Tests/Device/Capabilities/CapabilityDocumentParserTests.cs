using System.Linq;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Parser tests for <c>CONFigure:CAPabilities:JSON?</c> and
/// <c>CONFigure:CAPabilities:APIVersion?</c>, driven by a document captured from real hardware
/// (<see cref="CapabilityDocumentSamples"/>).
/// </summary>
public class CapabilityDocumentParserTests
{
    private static CapabilityDocument ParseBenchDocument()
    {
        Assert.True(CapabilityDocumentParser.TryParse(
            CapabilityDocumentSamples.Nyquist1Firmware372, out var document));
        return document!;
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsSchemaAndIdentity()
    {
        var document = ParseBenchDocument();

        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal("https://daqifi.com/schemas/capability/v1", document.SchemaUri);
        Assert.NotNull(document.Identity);
        Assert.Equal("DAQiFi", document.Identity!.Vendor);
        Assert.Equal("Nyquist", document.Identity.Model);
        Assert.Equal("NQ1", document.Identity.Variant);
        Assert.Equal("7E2815916200E898", document.Identity.Serial);
        Assert.Equal("3.7.2", document.Identity.FirmwareRevision);
        Assert.Equal("2.0.0", document.Identity.HardwareRevision);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsChannelCountsByKind()
    {
        var document = ParseBenchDocument();

        Assert.Equal(32, document.Channels.Count);
        Assert.Equal(16, document.CountChannels(CapabilityChannelKind.AnalogInput));
        Assert.Equal(0, document.CountChannels(CapabilityChannelKind.AnalogOutput));
        Assert.Equal(16, document.CountChannels(CapabilityChannelKind.DigitalIo));
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsAnalogInputDetail()
    {
        var document = ParseBenchDocument();

        var channel = document.Channels.Single(
            c => c.Kind == CapabilityChannelKind.AnalogInput && c.Id == 0);

        Assert.Equal("analog-input", channel.RawKind);
        Assert.Equal("voltage", channel.SignalType);
        Assert.Equal("V", channel.Unit);
        Assert.Equal(12, channel.ResolutionBits);
        Assert.False(channel.IsDifferential);
        Assert.Equal(0.0, channel.RangeMinimum);
        Assert.Equal(5.0, channel.RangeMaximum);
        Assert.Equal(1.0, channel.CalibrationSlope);
        Assert.Equal(0.0, channel.CalibrationIntercept);
        Assert.False(channel.SupportsPwm);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_IdentifiesDedicatedConverterChannels()
    {
        // The NQ1's Type-1 (dedicated-ADC, zero-skew) analog inputs. This is the count that
        // divides type1_aggregate_max_hz in the device's rate model.
        var document = ParseBenchDocument();

        var simultaneous = document.Channels
            .Where(c => c.Kind == CapabilityChannelKind.AnalogInput && c.IsSimultaneous)
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(new[] { 4, 8, 10, 12, 14 }, simultaneous);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_IdentifiesPwmCapableDigitalPins()
    {
        var document = ParseBenchDocument();

        var pwmCapable = document.Channels
            .Where(c => c.Kind == CapabilityChannelKind.DigitalIo && c.SupportsPwm)
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(new[] { 0, 3, 4, 5, 6, 7 }, pwmCapable);

        var pwmPin = document.Channels.Single(
            c => c.Kind == CapabilityChannelKind.DigitalIo && c.Id == 3);
        Assert.Equal(1, pwmPin.PwmMinimumFrequencyHz);
        Assert.Equal(50000, pwmPin.PwmMaximumFrequencyHz);

        // Absence of the "pwm" key is the negative answer — not a false-valued flag.
        var plainPin = document.Channels.Single(
            c => c.Kind == CapabilityChannelKind.DigitalIo && c.Id == 1);
        Assert.False(plainPin.SupportsPwm);
        Assert.Null(plainPin.PwmMinimumFrequencyHz);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsStreamingBlock()
    {
        var document = ParseBenchDocument();

        Assert.NotNull(document.Streaming);
        var streaming = document.Streaming!;
        Assert.Equal(1, streaming.MinimumSampleRateHz);
        Assert.Equal(22000, streaming.MaximumSampleRateHz);
        Assert.Equal(500, streaming.ConservativeEnvelopeHz);
        // Captured with no channels enabled: the firmware reports 0, and 0 is a real answer that
        // must survive parsing rather than collapse to "not stated".
        Assert.Equal(0, streaming.CurrentMaximumRateHz);
        Assert.Equal("error", streaming.RateValidation);
        Assert.Equal(new[] { "pb", "csv", "json" }, streaming.Encodings);
        Assert.Equal(new[] { "usb", "wifi", "sd" }, streaming.Transports);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsRateModelConstants()
    {
        var document = ParseBenchDocument();

        var model = document.Streaming!.RateModel;
        Assert.NotNull(model);
        Assert.Equal(22000, model!.AbsoluteMaximumHz);
        Assert.Equal(55000, model.Type1AggregateMaximumHz);
        Assert.Equal(110000, model.PerTickBudgetHz);
        Assert.Equal(6, model.PerTickOverhead);
        Assert.Contains("absolute_max_hz", model.Formula);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_ReadsStorageTransportAndPowerFlags()
    {
        var document = ParseBenchDocument();

        Assert.True(document.SdSupported);
        Assert.True(document.UsbSupported);
        Assert.True(document.WifiSupported);
        Assert.False(document.EthernetSupported);
        Assert.True(document.BatteryPresent);
        Assert.True(document.ExternalPowerSupported);
    }

    [Fact]
    public void TryParse_RealNyquist1Document_RetainsRawJson()
    {
        var document = ParseBenchDocument();

        Assert.Equal(CapabilityDocumentSamples.Nyquist1Firmware372, document.RawJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"schema_version\":2")]            // truncated mid-document
    [InlineData("[1,2,3]")]                          // valid JSON, wrong shape
    [InlineData("{\"identity\":{\"variant\":\"NQ1\"}}")] // no schema_version
    [InlineData("{\"schema_version\":\"two\"}")]     // schema_version is not a number
    public void TryParse_UnusableInput_ReturnsFalse(string? json)
    {
        Assert.False(CapabilityDocumentParser.TryParse(json, out var document));
        Assert.Null(document);
    }

    [Fact]
    public void TryParse_DocumentWithOnlySchemaVersion_ParsesWithEverythingUnstated()
    {
        // A document stripped to its one required field still parses; every optional block reads
        // as "not stated" so the merge leaves board-derived values alone.
        Assert.True(CapabilityDocumentParser.TryParse("{\"schema_version\":2}", out var document));

        Assert.Equal(2, document!.SchemaVersion);
        Assert.Null(document.Identity);
        Assert.Empty(document.Channels);
        Assert.Null(document.Streaming);
        Assert.Null(document.SdSupported);
        Assert.Null(document.UsbSupported);
        Assert.Null(document.WifiSupported);
    }

    [Fact]
    public void TryParse_FieldsWithUnexpectedTypes_ReadAsUnstated()
    {
        // Forward-compatibility: a retyped field must degrade to "not stated" rather than throw,
        // so the rest of the document still contributes.
        const string json = """
            {"schema_version":2,
             "storage":{"sd_supported":"yes"},
             "transports":{"usb":{"supported":1}},
             "streaming":{"sample_rate_range_hz":{"max":"fast"},"conservative_envelope_hz":500}}
            """;

        Assert.True(CapabilityDocumentParser.TryParse(json, out var document));

        Assert.Null(document!.SdSupported);
        Assert.Null(document.UsbSupported);
        Assert.Null(document.Streaming!.MaximumSampleRateHz);
        Assert.Equal(500, document.Streaming.ConservativeEnvelopeHz);
    }

    [Fact]
    public void TryParse_UnknownChannelKind_IsRetainedAsUnknown()
    {
        // Adding a channel kind is an additive change that does not bump the schema version, so an
        // unrecognized kind must not fail the parse or be miscounted as a known one.
        const string json = """
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-input"},
              {"id":0,"kind":"counter"}]}
            """;

        Assert.True(CapabilityDocumentParser.TryParse(json, out var document));

        Assert.Equal(2, document!.Channels.Count);
        Assert.Equal(1, document.CountChannels(CapabilityChannelKind.AnalogInput));
        Assert.Equal(0, document.CountChannels(CapabilityChannelKind.AnalogOutput));
        Assert.Equal(0, document.CountChannels(CapabilityChannelKind.DigitalIo));

        var unknown = document.Channels[1];
        Assert.Equal(CapabilityChannelKind.Unknown, unknown.Kind);
        Assert.Equal("counter", unknown.RawKind);
    }

    [Fact]
    public void TryParse_ChannelEntryMissingIdOrKind_IsSkipped()
    {
        const string json = """
            {"schema_version":2,"channels":[
              {"kind":"analog-input"},
              {"id":1},
              "not-an-object",
              {"id":2,"kind":"analog-input"}]}
            """;

        Assert.True(CapabilityDocumentParser.TryParse(json, out var document));

        var channel = Assert.Single(document!.Channels);
        Assert.Equal(2, channel.Id);
    }

    [Fact]
    public void TryParseLines_SkipsEchoAndPromptAndFindsDocument()
    {
        string[] lines =
        [
            "CONFigure:CAPabilities:JSON?",
            CapabilityDocumentSamples.Nyquist1Firmware372,
            "DAQIFI>"
        ];

        Assert.True(CapabilityDocumentParser.TryParseLines(lines, out var document));
        Assert.Equal("NQ1", document!.Identity!.Variant);
    }

    [Fact]
    public void TryParseLines_WithoutADocumentLine_ReturnsFalse()
    {
        string[] lines = ["CONFigure:CAPabilities:JSON?", "**ERROR: -113, \"Undefined header\"", "DAQIFI>"];

        Assert.False(CapabilityDocumentParser.TryParseLines(lines, out var document));
        Assert.Null(document);
    }

    [Fact]
    public void TryParseLines_UnparseableJsonLineDoesNotMaskALaterValidOne()
    {
        string[] lines = ["{\"unrelated\":true}", CapabilityDocumentSamples.Nyquist1Firmware372];

        Assert.True(CapabilityDocumentParser.TryParseLines(lines, out var document));
        Assert.Equal(2, document!.SchemaVersion);
    }

    [Fact]
    public void TryParseApiVersion_ReadsTheBareIntegerReply()
    {
        string[] lines = ["CONFigure:CAPabilities:APIVersion?", "2", "DAQIFI>"];

        Assert.True(CapabilityDocumentParser.TryParseApiVersion(lines, out var apiVersion));
        Assert.Equal(2, apiVersion);
    }

    [Fact]
    public void TryParseApiVersion_OnUndefinedHeaderError_ReturnsFalse()
    {
        // What a below-floor device replies: the query does not exist, so there is no version.
        string[] lines = ["**ERROR: -113, \"Undefined header\""];

        Assert.False(CapabilityDocumentParser.TryParseApiVersion(lines, out var apiVersion));
        Assert.Equal(0, apiVersion);
    }

    [Fact]
    public void TryParseApiVersion_WithNoLines_ReturnsFalse()
    {
        Assert.False(CapabilityDocumentParser.TryParseApiVersion([], out var apiVersion));
        Assert.Equal(0, apiVersion);
    }
}
