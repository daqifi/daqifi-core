using System.Linq;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Parser tests for the capability-document fields firmware v3.8.0 and v3.9.0 added: the rate cap
/// terms, the streaming timebase, each channel's scan offset, the calibration status, the firmware
/// image CRC, and <c>null</c> for a non-finite calibration coefficient (#957).
/// </summary>
public class CapabilityDocumentFirmware390Tests
{
    private static CapabilityDocument Parse(string json)
    {
        Assert.True(CapabilityDocumentParser.TryParse(json, out var document));
        return document!;
    }

    private static CapabilityChannel AnalogInput(CapabilityDocument document, int id) =>
        document.Channels.Single(c => c.Kind == CapabilityChannelKind.AnalogInput && c.Id == id);

    [Fact]
    public void TryParse_Firmware390Document_ReadsCapTermsAndNamesTheBindingOne()
    {
        var streaming = Parse(CapabilityDocumentSamples.Nyquist1Firmware390).Streaming!;

        var terms = streaming.CapTerms;
        Assert.NotNull(terms);
        Assert.Equal(15714, terms!.AdcAdditiveHz);
        Assert.Equal(12000, terms.TransportHz);
        Assert.Equal(31446, terms.ScanBoundHz);
        // 0 is "not applicable" (the stream is not going to SD), kept as 0 rather than null.
        Assert.Equal(0, terms.SdAdditiveHz);

        Assert.Equal(CapabilityRateCapTerm.Transport, terms.BindingTerm);
        Assert.Equal(12000, streaming.CurrentMaximumRateHz);
    }

    [Fact]
    public void TryParse_Firmware390Document_ReadsTimingBlock()
    {
        var timing = Parse(CapabilityDocumentSamples.Nyquist1Firmware390).Streaming!.Timing;

        Assert.NotNull(timing);
        Assert.Equal(42_000_000, timing!.TimestampHz);
        Assert.Equal(10_500_000, timing.StreamTimerHz);
        Assert.Equal(42_000, timing.TimestampTicksPerSample);
        Assert.Equal(1_000_000, timing.ActualRateMillihertz);
        Assert.Equal(84_000_000, timing.PeripheralClockHz);
        Assert.Equal(84_000_000, timing.PeripheralClockBuiltHz);
        Assert.True(timing.ClockMatchesBuild);
    }

    [Fact]
    public void TryParse_Firmware390Document_ReadsEachChannelsScanOffset()
    {
        var document = Parse(CapabilityDocumentSamples.Nyquist1Firmware390);

        // Scan positions 0 and 1 of the enabled multiplexed inputs.
        Assert.Equal(510, AnalogInput(document, 0).ScanOffsetTicks);
        Assert.Equal(1090, AnalogInput(document, 1).ScanOffsetTicks);

        // Enabled but dedicated: converts with the stamp, so no offset.
        var dedicated = AnalogInput(document, 4);
        Assert.True(dedicated.IsSimultaneous);
        Assert.Equal(0, dedicated.ScanOffsetTicks);

        // Not in the scan: 0 too, and still a stated value rather than null.
        Assert.Equal(0, AnalogInput(document, 2).ScanOffsetTicks);

        // Only analog inputs carry the field.
        Assert.All(
            document.Channels.Where(c => c.Kind == CapabilityChannelKind.DigitalIo),
            c => Assert.Null(c.ScanOffsetTicks));

        // The offset is in timestamp ticks: 510 ticks of 42 MHz is about 12.1 microseconds.
        var seconds = (double)AnalogInput(document, 0).ScanOffsetTicks!.Value
                      / document.Streaming!.Timing!.TimestampHz!.Value;
        Assert.Equal(12.14e-6, seconds, 1e-8);
    }

    [Fact]
    public void TryParse_Firmware390Document_ReadsCalibrationStatusAndFirmwareCrc()
    {
        var document = Parse(CapabilityDocumentSamples.Nyquist1Firmware390);

        var identity = document.Identity!;
        Assert.Equal("3.9.0", identity.FirmwareRevision);
        Assert.Equal("3F0A6B1D", identity.FirmwareCrc32);

        // A board that was never calibrated, which firmware before 3.9.0 could not report.
        Assert.NotNull(identity.Calibration);
        Assert.False(identity.Calibration!.FactoryPresent);
        Assert.Equal(CapabilityCalibrationSource.None, identity.Calibration.Source);

        // %.17g writes identity coefficients as the bare integers 1 and 0; both read as doubles.
        var channel = AnalogInput(document, 0);
        Assert.Equal(1.0, channel.CalibrationSlope);
        Assert.Equal(0.0, channel.CalibrationIntercept);
    }

    [Fact]
    public void TryParse_Firmware390Document_StillReadsEverythingEarlierFirmwareStated()
    {
        var document = Parse(CapabilityDocumentSamples.Nyquist1Firmware390);

        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal("NQ1", document.Identity!.Variant);
        Assert.Equal(16, document.CountChannels(CapabilityChannelKind.AnalogInput));
        Assert.Equal(16, document.CountChannels(CapabilityChannelKind.DigitalIo));
        Assert.Equal(22000, document.Streaming!.MaximumSampleRateHz);
        Assert.Equal(55000, document.Streaming.RateModel!.Type1AggregateMaximumHz);
        Assert.Equal(new[] { "pb", "csv", "json", "csv_compact" }, document.Streaming.Encodings);
        Assert.True(document.SdSupported);
    }

    [Fact]
    public void TryParse_Firmware380Document_StatesTimingAndCrcButNot390Fields()
    {
        var document = Parse(CapabilityDocumentSamples.Nyquist1Firmware380);

        Assert.Equal("3.8.0", document.Identity!.FirmwareRevision);
        Assert.Equal("9C41D2E7", document.Identity.FirmwareCrc32);

        var timing = document.Streaming!.Timing;
        Assert.NotNull(timing);
        Assert.Equal(42_000_000, timing!.TimestampHz);
        // No rate configured since boot: the per-rate values are a real 0, not unstated.
        Assert.Equal(0, timing.TimestampTicksPerSample);
        Assert.Equal(0, timing.ActualRateMillihertz);

        Assert.Null(document.Identity.Calibration);
        Assert.Null(document.Streaming.CapTerms);
        Assert.All(document.Channels, c => Assert.Null(c.ScanOffsetTicks));
    }

    [Fact]
    public void TryParse_Firmware372Document_LeavesEveryNewFieldUnstated()
    {
        var document = Parse(CapabilityDocumentSamples.Nyquist1Firmware372);

        Assert.Null(document.Identity!.FirmwareCrc32);
        Assert.Null(document.Identity.Calibration);
        Assert.Null(document.Streaming!.CapTerms);
        Assert.Null(document.Streaming.Timing);
        Assert.All(document.Channels, c => Assert.Null(c.ScanOffsetTicks));
    }

    [Theory]
    [InlineData("\"factory\"", CapabilityCalibrationSource.Factory)]
    [InlineData("\"user\"", CapabilityCalibrationSource.User)]
    [InlineData("\"none\"", CapabilityCalibrationSource.None)]
    [InlineData("\"unknown\"", CapabilityCalibrationSource.Unknown)]
    // A value added after this version establishes nothing either, so it is Unknown, never None.
    [InlineData("\"provisional\"", CapabilityCalibrationSource.Unknown)]
    [InlineData("\"FACTORY\"", CapabilityCalibrationSource.Unknown)]
    public void TryParse_CalibrationSource_MapsEachWireValue(string wireValue, CapabilityCalibrationSource expected)
    {
        var document = Parse(
            "{\"schema_version\":2,\"identity\":{\"cal\":{\"factory_present\":true,\"source\":" + wireValue + "}}}");

        Assert.True(document.Identity!.Calibration!.FactoryPresent);
        Assert.Equal(expected, document.Identity.Calibration.Source);
    }

    [Theory]
    [InlineData("{\"schema_version\":2,\"identity\":{\"cal\":{\"factory_present\":true}}}")]
    [InlineData("{\"schema_version\":2,\"identity\":{\"cal\":{\"factory_present\":true,\"source\":2}}}")]
    [InlineData("{\"schema_version\":2,\"identity\":{\"cal\":{\"factory_present\":true,\"source\":null}}}")]
    public void TryParse_CalibrationSourceMissingOrMistyped_ReadsAsUnstated(string json)
    {
        var calibration = Parse(json).Identity!.Calibration!;

        Assert.True(calibration.FactoryPresent);
        Assert.Null(calibration.Source);
    }

    [Fact]
    public void TryParse_NewBlocksWithUnexpectedShape_ReadAsUnstated()
    {
        const string json = """
            {"schema_version":2,
             "identity":{"cal":"factory","firmware_crc32":12345678},
             "channels":[{"id":0,"kind":"analog-input","scan_offset_ticks":"510"}],
             "streaming":{"cap_terms":[1,2,3,4],"timing":{"timestamp_hz":"42MHz","clock_ok":1},
                          "current_max_rate_hz":500}}
            """;

        var document = Parse(json);

        Assert.Null(document.Identity!.Calibration);
        Assert.Null(document.Identity.FirmwareCrc32);
        Assert.Null(document.Channels.Single().ScanOffsetTicks);
        Assert.Null(document.Streaming!.CapTerms);
        Assert.NotNull(document.Streaming.Timing);
        Assert.Null(document.Streaming.Timing!.TimestampHz);
        Assert.Null(document.Streaming.Timing.ClockMatchesBuild);
        Assert.Equal(500, document.Streaming.CurrentMaximumRateHz);
    }

    [Fact]
    public void TryParse_NonFiniteCalibrationWrittenAsNull_ReadsAsUnstatedAndKeepsTheRest()
    {
        // Firmware 3.9.0 writes a coefficient a client set to infinity as JSON null (before that,
        // it wrote "inf", which made the whole document unparseable). One channel's null must not
        // take the document, or the neighbouring channels, down with it.
        var json = CapabilityDocumentSamples.Nyquist1Firmware390.Replace(
            "\"scan_offset_ticks\":1090,\"ranges\":[{\"min\":0.000,\"max\":5.000}],"
            + "\"calibration\":{\"model\":\"linear\",\"user_override_supported\":true,\"slope\":1,\"intercept\":0}",
            "\"scan_offset_ticks\":1090,\"ranges\":[{\"min\":null,\"max\":null}],"
            + "\"calibration\":{\"model\":\"linear\",\"user_override_supported\":true,\"slope\":null,\"intercept\":null}",
            System.StringComparison.Ordinal);
        Assert.NotEqual(CapabilityDocumentSamples.Nyquist1Firmware390, json);

        var document = Parse(json);

        var affected = AnalogInput(document, 1);
        Assert.Null(affected.CalibrationSlope);
        Assert.Null(affected.CalibrationIntercept);
        Assert.Null(affected.RangeMinimum);
        Assert.Null(affected.RangeMaximum);
        Assert.Equal(1090, affected.ScanOffsetTicks);

        var neighbour = AnalogInput(document, 0);
        Assert.Equal(1.0, neighbour.CalibrationSlope);
        Assert.Equal(5.0, neighbour.RangeMaximum);
        Assert.Equal(32, document.Channels.Count);
    }

    [Fact]
    public void TryParse_FullPrecisionCalibration_RoundTrips()
    {
        // %.17g is the firmware's round-trip width for a double; the parser must keep all of it.
        const string json = """
            {"schema_version":2,"channels":[{"id":0,"kind":"analog-input",
             "calibration":{"slope":0.0012207031250000001,"intercept":-1.2345678901234567e-308}}]}
            """;

        var channel = Parse(json).Channels.Single();

        Assert.Equal(0.0012207031250000001, channel.CalibrationSlope);
        Assert.Equal(-1.2345678901234567e-308, channel.CalibrationIntercept);
    }
}
