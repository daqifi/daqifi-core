using System;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Tests that a capability document <i>overlays</i> board-derived
/// <see cref="DeviceCapabilities"/> rather than replacing them (ADR 0001, Decision 2 item 4).
/// </summary>
public class CapabilityDocumentMergeTests
{
    private static CapabilityDocument BenchDocument()
    {
        Assert.True(CapabilityDocumentParser.TryParse(
            CapabilityDocumentSamples.Nyquist1Firmware372, out var document));
        return document!;
    }

    private static CapabilityDocument Parse(string json)
    {
        Assert.True(CapabilityDocumentParser.TryParse(json, out var document));
        return document!;
    }

    [Fact]
    public void MergeInto_RealNyquist1Document_AgreesWithTheBoardTable()
    {
        // The bench check the issue asks for, pinned as a regression test: for a board the static
        // table already describes, the device's own answer must not contradict it.
        var boardDerived = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        var merged = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        BenchDocument().MergeInto(merged);

        Assert.Equal(boardDerived.HasSdCard, merged.HasSdCard);
        Assert.Equal(boardDerived.HasWiFi, merged.HasWiFi);
        Assert.Equal(boardDerived.HasUsb, merged.HasUsb);
        Assert.Equal(boardDerived.SupportsStreaming, merged.SupportsStreaming);
    }

    [Fact]
    public void MergeInto_RealNyquist1Document_FillsChannelCountsAndRaisesTheRateCeiling()
    {
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        BenchDocument().MergeInto(capabilities);

        Assert.Equal(16, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);

        // The one field where the device disagrees with the table, and the reason this reader
        // exists: the hardcoded 1000 Hz predates firmware v3.5.0 removing the muxed scan-rate cap.
        Assert.Equal(22000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void MergeInto_LeavesFieldsTheSchemaDoesNotCarry()
    {
        // The schema publishes what a client can do, not what parts are fitted, so it has no
        // WINC-module fact and no "streaming supported" boolean. Both stay board-derived.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        BenchDocument().MergeInto(capabilities);

        Assert.True(capabilities.HasWincWifiModule);
        Assert.True(capabilities.SupportsStreaming);
    }

    [Fact]
    public void MergeInto_DocumentThatStatesNothing_LeavesEveryBoardValueIntact()
    {
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist3);
        capabilities.AnalogInputChannels = 8;
        capabilities.AnalogOutputChannels = 2;
        capabilities.DigitalChannels = 16;

        Parse("{\"schema_version\":2}").MergeInto(capabilities);

        Assert.True(capabilities.HasSdCard);
        Assert.True(capabilities.HasWiFi);
        Assert.True(capabilities.HasUsb);
        Assert.True(capabilities.HasWincWifiModule);
        Assert.Equal(8, capabilities.AnalogInputChannels);
        Assert.Equal(2, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void MergeInto_PartialDocument_OverlaysOnlyWhatItStates()
    {
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        capabilities.AnalogInputChannels = 16;

        // States storage only. Everything else — transports, channels, streaming — must fall back.
        Parse("{\"schema_version\":2,\"storage\":{\"sd_supported\":false}}").MergeInto(capabilities);

        Assert.False(capabilities.HasSdCard);
        Assert.True(capabilities.HasWiFi);
        Assert.True(capabilities.HasUsb);
        Assert.Equal(16, capabilities.AnalogInputChannels);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void MergeInto_ChannelArrayIsOverlaidAsASet()
    {
        // channels[] is the board's complete channel list, so once it is present a count of zero
        // for a kind is a real answer rather than a gap to fall back on.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        capabilities.AnalogInputChannels = 99;
        capabilities.AnalogOutputChannels = 99;
        capabilities.DigitalChannels = 99;

        Parse("{\"schema_version\":2,\"channels\":[{\"id\":0,\"kind\":\"analog-input\"}]}")
            .MergeInto(capabilities);

        Assert.Equal(1, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
    }

    [Fact]
    public void MergeInto_NonPositiveMaximumSampleRate_DoesNotLowerTheCeiling()
    {
        // A 0 or negative ceiling would make every streaming frequency invalid; keep the board's.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        Parse("{\"schema_version\":2,\"streaming\":{\"sample_rate_range_hz\":{\"min\":1,\"max\":0}}}")
            .MergeInto(capabilities);

        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void MergeInto_UnknownBoard_DoesNotTurnHardwareFlagsOffThatTheDocumentOmits()
    {
        // Preserves the ADR 0001 rule that all-false capabilities on an Unknown board mean
        // "not yet known", not "hardware absent": an omitted field never flips a flag.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Unknown);

        Parse("{\"schema_version\":2,\"transports\":{\"usb\":{\"supported\":true}}}")
            .MergeInto(capabilities);

        Assert.True(capabilities.HasUsb);
        Assert.False(capabilities.HasSdCard);
        Assert.False(capabilities.HasWiFi);
    }

    [Fact]
    public void MergeInto_NullCapabilities_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BenchDocument().MergeInto(null!));
    }
}
