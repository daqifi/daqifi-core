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
    public void OverlayOnto_RealNyquist1Document_AgreesWithTheBoardTable()
    {
        // The board table is the floor under the live document, so for a board it describes the
        // device's own answer must not contradict it — in any field the document states. A table
        // that drifts from the hardware fails here (#904).
        var boardDerived = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        var merged = BenchDocument().OverlayOnto(boardDerived);

        Assert.Equal(boardDerived.HasSdCard, merged.HasSdCard);
        Assert.Equal(boardDerived.HasWifi, merged.HasWifi);
        Assert.Equal(boardDerived.HasUsb, merged.HasUsb);
        Assert.Equal(boardDerived.SupportsStreaming, merged.SupportsStreaming);
        Assert.Equal(boardDerived.AnalogInputChannels, merged.AnalogInputChannels);
        Assert.Equal(boardDerived.AnalogOutputChannels, merged.AnalogOutputChannels);
        Assert.Equal(boardDerived.DigitalChannels, merged.DigitalChannels);
        Assert.Equal(boardDerived.MaxSamplingRate, merged.MaxSamplingRate);
    }

    [Fact]
    public void OverlayOnto_RealNyquist1Document_FillsChannelCountsAndTheRateCeilingOnABlankInstance()
    {
        var capabilities = BenchDocument().OverlayOnto(new DeviceCapabilities());

        Assert.Equal(16, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(22000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void OverlayOnto_LeavesFieldsTheSchemaDoesNotCarry()
    {
        // The schema publishes what a client can do, not what parts are fitted, so it has no
        // WINC-module fact and no "streaming supported" boolean. Both stay board-derived.
        var capabilities = BenchDocument().OverlayOnto(DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1));

        Assert.True(capabilities.HasWincWifiModule);
        Assert.True(capabilities.SupportsStreaming);
    }

    [Fact]
    public void OverlayOnto_DocumentThatStatesNothing_LeavesEveryBoardValueIntact()
    {
        var board = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist3) with
        {
            AnalogInputChannels = 8,
            AnalogOutputChannels = 2,
            DigitalChannels = 16
        };

        var capabilities = Parse("{\"schema_version\":2}").OverlayOnto(board);

        Assert.True(capabilities.HasSdCard);
        Assert.True(capabilities.HasWifi);
        Assert.True(capabilities.HasUsb);
        Assert.True(capabilities.HasWincWifiModule);
        Assert.Equal(8, capabilities.AnalogInputChannels);
        Assert.Equal(2, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(16000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void OverlayOnto_PartialDocument_OverlaysOnlyWhatItStates()
    {
        var board = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1) with
        {
            AnalogInputChannels = 16
        };

        // States storage only. Everything else — transports, channels, streaming — must fall back.
        var capabilities = Parse("{\"schema_version\":2,\"storage\":{\"sd_supported\":false}}").OverlayOnto(board);

        Assert.False(capabilities.HasSdCard);
        Assert.True(capabilities.HasWifi);
        Assert.True(capabilities.HasUsb);
        Assert.Equal(16, capabilities.AnalogInputChannels);
        Assert.Equal(22000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void OverlayOnto_ChannelArrayIsOverlaidAsASet()
    {
        // channels[] is the board's complete channel list, so once it is present a count of zero
        // for a kind is a real answer rather than a gap to fall back on.
        var board = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1) with
        {
            AnalogInputChannels = 99,
            AnalogOutputChannels = 99,
            DigitalChannels = 99
        };

        var capabilities = Parse("{\"schema_version\":2,\"channels\":[{\"id\":0,\"kind\":\"analog-input\"}]}")
            .OverlayOnto(board);

        Assert.Equal(1, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
    }

    [Fact]
    public void OverlayOnto_NonPositiveMaximumSampleRate_DoesNotLowerTheCeiling()
    {
        // A 0 or negative ceiling would make every streaming frequency invalid; keep the board's.
        var board = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        var capabilities = Parse("{\"schema_version\":2,\"streaming\":{\"sample_rate_range_hz\":{\"min\":1,\"max\":0}}}")
            .OverlayOnto(board);

        Assert.Equal(22000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void OverlayOnto_UnknownBoard_DoesNotTurnHardwareFlagsOffThatTheDocumentOmits()
    {
        // Preserves the ADR 0001 rule that all-false capabilities on an Unknown board mean
        // "not yet known", not "hardware absent": an omitted field never flips a flag.
        var board = DeviceCapabilities.FromDeviceType(DeviceType.Unknown);

        var capabilities = Parse("{\"schema_version\":2,\"transports\":{\"usb\":{\"supported\":true}}}")
            .OverlayOnto(board);

        Assert.True(capabilities.HasUsb);
        Assert.False(capabilities.HasSdCard);
        Assert.False(capabilities.HasWifi);
    }

    [Fact]
    public void OverlayOnto_NullCapabilities_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BenchDocument().OverlayOnto(null!));
    }
}
