using Daqifi.Mcp;

namespace Daqifi.Mcp.Tests;

public class SampleRateCapCalculatorTests
{
    #region ComputeCapHz

    [Fact]
    public void ComputeCapHz_NoCapabilityDocument_FallsBackToHardwareMax()
    {
        Assert.Equal(22000, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: null, maxSampleRateHzOption: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ComputeCapHz_NonPositiveHardwareMax_IsFlooredToOne(int hardwareMax)
    {
        Assert.Equal(1, SampleRateCapCalculator.ComputeCapHz(hardwareMax, currentMaxRateHz: null, maxSampleRateHzOption: null));
    }

    [Fact]
    public void ComputeCapHz_CurrentMaxBelowHardwareMax_UsesCurrentMax()
    {
        // #447 bench figures: 1 analog channel -> 7746 Hz cap; 16 channels -> 3518 Hz cap.
        Assert.Equal(7746, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 7746, maxSampleRateHzOption: null));
        Assert.Equal(3518, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 3518, maxSampleRateHzOption: null));
    }

    [Fact]
    public void ComputeCapHz_CurrentMaxAboveHardwareMax_IsBoundedToHardwareMax()
    {
        // A self-inconsistent document (or a channel-set read racing a board-table update)
        // must never report a cap above the absolute ISR ceiling.
        Assert.Equal(22000, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 50000, maxSampleRateHzOption: null));
    }

    [Fact]
    public void ComputeCapHz_ZeroCurrentMax_IsARealAnswerNotFlooredToOne()
    {
        // Zero enabled channels genuinely caps the rate at 0 — unlike hardwareMax, this is not
        // treated as an uninitialized/invalid value.
        Assert.Equal(0, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 0, maxSampleRateHzOption: null));
    }

    [Fact]
    public void ComputeCapHz_ServerOptionBelowDeviceCap_Wins()
    {
        Assert.Equal(500, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 7746, maxSampleRateHzOption: 500));
    }

    [Fact]
    public void ComputeCapHz_ServerOptionAboveDeviceCap_DeviceCapStillWins()
    {
        Assert.Equal(7746, SampleRateCapCalculator.ComputeCapHz(22000, currentMaxRateHz: 7746, maxSampleRateHzOption: 50000));
    }

    #endregion

    #region EnforceCap

    [Fact]
    public void EnforceCap_RateAtOrBelowCap_NoAdjustment()
    {
        Assert.Equal((3518, (int?)null), SampleRateCapCalculator.EnforceCap(3518, capHz: 3518));
        Assert.Equal((1000, (int?)null), SampleRateCapCalculator.EnforceCap(1000, capHz: 3518));
    }

    [Fact]
    public void EnforceCap_RateAboveCap_LowersToCapAndReportsPreviousRate()
    {
        // The exact reorder-trap sequence from #447: configure_analog_channels([0]) -> cap 7746;
        // set_sample_rate(7746) -> accepted; configure_analog_channels([0..15]) -> cap drops to
        // 3518 while the live rate is still 7746.
        var (newRateHz, adjustedFromHz) = SampleRateCapCalculator.EnforceCap(7746, capHz: 3518);

        Assert.Equal(3518, newRateHz);
        Assert.Equal(7746, adjustedFromHz);
    }

    [Fact]
    public void EnforceCap_ZeroCap_LeavesRateUnchanged()
    {
        // A cap of 0 (nothing enabled) must not drive the live rate to 0 — see #447's suggested
        // fix. The rate is stale until the channel set changes again, not invalid.
        var (newRateHz, adjustedFromHz) = SampleRateCapCalculator.EnforceCap(7746, capHz: 0);

        Assert.Equal(7746, newRateHz);
        Assert.Null(adjustedFromHz);
    }

    #endregion
}
