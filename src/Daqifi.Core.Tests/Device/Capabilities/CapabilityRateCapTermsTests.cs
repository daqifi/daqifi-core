using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Tests for <see cref="CapabilityRateCapTerms.BindingTerm"/>: the lowest term above zero names the
/// limit, and 0 means "not applicable", never a 0 Hz cap.
/// </summary>
public class CapabilityRateCapTermsTests
{
    [Theory]
    [InlineData(9000, 18000, 31000, 0, CapabilityRateCapTerm.AdcAdditive)]
    [InlineData(15000, 12000, 31000, 0, CapabilityRateCapTerm.Transport)]
    [InlineData(15000, 18000, 6000, 0, CapabilityRateCapTerm.ScanBound)]
    [InlineData(15000, 18000, 31000, 4000, CapabilityRateCapTerm.SdAdditive)]
    // A zero term is skipped, not treated as the lowest.
    [InlineData(15000, 18000, 0, 0, CapabilityRateCapTerm.AdcAdditive)]
    [InlineData(0, 18000, 0, 0, CapabilityRateCapTerm.Transport)]
    public void BindingTerm_IsTheLowestTermAboveZero(
        int adcHz, int transportHz, int scanHz, int sdHz, CapabilityRateCapTerm expected)
    {
        var terms = new CapabilityRateCapTerms
        {
            AdcAdditiveHz = adcHz,
            TransportHz = transportHz,
            ScanBoundHz = scanHz,
            SdAdditiveHz = sdHz
        };

        Assert.Equal(expected, terms.BindingTerm);
    }

    [Fact]
    public void BindingTerm_WhenEveryTermIsZero_IsNull()
    {
        // What the firmware reports with no input enabled: nothing binds.
        var terms = new CapabilityRateCapTerms
        {
            AdcAdditiveHz = 0,
            TransportHz = 0,
            ScanBoundHz = 0,
            SdAdditiveHz = 0
        };

        Assert.Null(terms.BindingTerm);
    }

    [Fact]
    public void BindingTerm_WhenNoTermIsStated_IsNull()
    {
        Assert.Null(new CapabilityRateCapTerms().BindingTerm);
    }

    [Fact]
    public void BindingTerm_SkipsUnstatedAndNegativeTerms()
    {
        var terms = new CapabilityRateCapTerms
        {
            AdcAdditiveHz = null,
            TransportHz = -1,
            ScanBoundHz = 31000
        };

        Assert.Equal(CapabilityRateCapTerm.ScanBound, terms.BindingTerm);
    }

    [Fact]
    public void BindingTerm_OnATie_IsTheFirstInEnumOrder()
    {
        var terms = new CapabilityRateCapTerms
        {
            AdcAdditiveHz = 18000,
            TransportHz = 12000,
            ScanBoundHz = 12000,
            SdAdditiveHz = 12000
        };

        Assert.Equal(CapabilityRateCapTerm.Transport, terms.BindingTerm);
    }
}
