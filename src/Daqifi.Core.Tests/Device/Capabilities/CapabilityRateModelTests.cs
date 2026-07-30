using System;
using System.Linq;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Tests for the client-side evaluation of the device's published streaming-rate formula.
/// </summary>
public class CapabilityRateModelTests
{
    /// <summary>The bench NQ1's constants, as captured on firmware 3.7.2.</summary>
    private static CapabilityRateModel BenchModel()
    {
        Assert.True(CapabilityDocumentParser.TryParse(
            CapabilityDocumentSamples.Nyquist1Firmware372, out var document));
        return document!.Streaming!.RateModel!;
    }

    [Theory]
    // min(22000, -, 110000/(6+4)) — muxed-only selection, budget term binds.
    [InlineData(0, 4, 11000)]
    // min(22000, 55000/1, 110000/(6+1)) — one dedicated channel; budget still binds.
    [InlineData(1, 1, 15714)]
    // min(22000, 55000/5, 110000/(6+5)) — five dedicated channels; Type-1 aggregate binds.
    [InlineData(5, 5, 10000)]
    // min(22000, 55000/5, 110000/(6+16)) — the whole board; budget binds hardest.
    [InlineData(5, 16, 5000)]
    // Nothing selected: only the absolute ceiling and the overhead-only budget term apply.
    [InlineData(0, 0, 18333)]
    public void TryComputeMaxRateHz_EvaluatesTheDeviceFormula(
        int simultaneousCount, int totalCount, int expected)
    {
        Assert.True(BenchModel().TryComputeMaxRateHz(simultaneousCount, totalCount, out var maxRateHz));
        Assert.Equal(expected, maxRateHz);
    }

    [Fact]
    public void TryComputeMaxRateHz_NeverExceedsTheAbsoluteCeiling()
    {
        var model = new CapabilityRateModel
        {
            AbsoluteMaximumHz = 22000,
            Type1AggregateMaximumHz = 55000,
            PerTickBudgetHz = 110000,
            PerTickOverhead = 0
        };

        Assert.True(model.TryComputeMaxRateHz(0, 1, out var maxRateHz));
        Assert.Equal(22000, maxRateHz);
    }

    [Fact]
    public void TryComputeMaxRateHz_WithNoConstants_ReturnsFalse()
    {
        Assert.False(new CapabilityRateModel().TryComputeMaxRateHz(2, 4, out var maxRateHz));
        Assert.Equal(0, maxRateHz);
    }

    [Fact]
    public void TryComputeMaxRateHz_WithOnlySomeConstants_UsesTheTermsItHas()
    {
        var model = new CapabilityRateModel { AbsoluteMaximumHz = 16000 };

        Assert.True(model.TryComputeMaxRateHz(4, 16, out var maxRateHz));
        Assert.Equal(16000, maxRateHz);
    }

    [Fact]
    public void TryComputeMaxRateHz_MuxedOnlySelection_IsNotCappedByTheType1Term()
    {
        // Dividing the Type-1 aggregate by a zero simultaneous count is undefined; treating the
        // term as zero would cap a muxed-only selection at 0 Hz. It must simply not apply.
        var model = new CapabilityRateModel
        {
            AbsoluteMaximumHz = 22000,
            Type1AggregateMaximumHz = 55000,
            PerTickBudgetHz = 110000,
            PerTickOverhead = 6
        };

        Assert.True(model.TryComputeMaxRateHz(0, 4, out var maxRateHz));
        Assert.Equal(11000, maxRateHz);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(2, -1)]
    [InlineData(5, 4)]
    public void TryComputeMaxRateHz_ImpossibleSelection_Throws(int simultaneousCount, int totalCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BenchModel().TryComputeMaxRateHz(simultaneousCount, totalCount, out _));
    }

    [Fact]
    public void TryComputeMaxRateHz_AgreesWithTheDeviceForTheWholeBoard()
    {
        // Feed the model the counts derived from the same document's channels[], the way a client
        // building a rate preview would, and check it lands on the documented rate model rather
        // than on hand-copied constants.
        Assert.True(CapabilityDocumentParser.TryParse(
            CapabilityDocumentSamples.Nyquist1Firmware372, out var document));

        var analogInputs = document!.Channels
            .Where(c => c.Kind == CapabilityChannelKind.AnalogInput)
            .ToArray();
        var simultaneousCount = analogInputs.Count(c => c.IsSimultaneous);

        Assert.True(document.Streaming!.RateModel!.TryComputeMaxRateHz(
            simultaneousCount, analogInputs.Length, out var maxRateHz));

        // Optimistic by construction — it excludes the transport cap — so it must sit at or above
        // the conservative envelope and at or below the absolute ceiling.
        Assert.InRange(maxRateHz, document.Streaming.ConservativeEnvelopeHz!.Value, 22000);
    }
}
