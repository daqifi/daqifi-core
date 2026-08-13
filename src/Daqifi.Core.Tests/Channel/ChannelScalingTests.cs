using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.Channel;

/// <summary>
/// Unit tests for <see cref="ChannelScaling"/>, the engineering-unit conversion a caller attaches
/// to a channel (#501).
/// </summary>
/// <remarks>
/// The split of responsibility this file pins: coefficients are validated at <b>construction</b>,
/// on the caller's thread, where an exception is a usable error message; <see cref="ChannelScaling.Apply"/>
/// runs on the decode thread and therefore never throws and never emits a non-finite value. Both
/// halves are needed — the constructor cannot see a value that only overflows for one reading.
/// </remarks>
public class ChannelScalingTests
{
    [Fact]
    public void Constructor_KeepsGainAndOffset()
    {
        var scaling = new ChannelScaling(gain: 20.0, offset: -1.5, unit: "PSI");

        Assert.Equal(20.0, scaling.Gain);
        Assert.Equal(-1.5, scaling.Offset);
        Assert.Equal("PSI", scaling.Unit);
    }

    [Fact]
    public void Constructor_WithOnlyAGain_DefaultsOffsetAndUnit()
    {
        var scaling = new ChannelScaling(2.0);

        Assert.Equal(0.0, scaling.Offset);
        Assert.Null(scaling.Unit);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_WithNonFiniteGain_Throws(double gain)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelScaling(gain));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_WithNonFiniteOffset_Throws(double offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelScaling(1.0, offset));
    }

    [Fact]
    public void Constructor_WithZeroGain_IsAllowed()
    {
        // Degenerate but not an error: a caller who wants a constant is entitled to one, and
        // rejecting it would be a trap with no safety benefit. Documented on the property.
        var scaling = new ChannelScaling(gain: 0.0, offset: 7.0);

        Assert.Equal(7.0, scaling.Apply(1234.5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankUnit_NormalizesToNull(string? unit)
    {
        // "Not stated" gets exactly one representation, so a consumer never has to test for both
        // null and "".
        Assert.Null(new ChannelScaling(1.0, 0.0, unit).Unit);
    }

    [Fact]
    public void Constructor_TrimsTheUnit()
    {
        Assert.Equal("PSI", new ChannelScaling(1.0, 0.0, "  PSI  ").Unit);
    }

    [Fact]
    public void Apply_IsGainThenOffset()
    {
        var scaling = new ChannelScaling(gain: 20.0, offset: 3.0);

        Assert.Equal(53.0, scaling.Apply(2.5));
    }

    [Fact]
    public void Apply_OnAnOverflowingResult_ReturnsTheUnscaledValue()
    {
        // The guarantee that keeps the decode thread honest: a configuration whose arithmetic blows
        // up for one reading yields that reading unscaled, rather than filling the stream with
        // Infinity or throwing where nobody can catch it.
        var scaling = new ChannelScaling(gain: 1e308);

        Assert.Equal(1e10, scaling.Apply(1e10));
    }

    [Fact]
    public void Apply_OnANonFiniteInput_ReturnsTheInput()
    {
        var scaling = new ChannelScaling(gain: 2.0);

        Assert.True(double.IsNaN(scaling.Apply(double.NaN)));
        Assert.Equal(double.PositiveInfinity, scaling.Apply(double.PositiveInfinity));
    }

    [Fact]
    public void Identity_LeavesValuesAlone_AndStatesNoUnit()
    {
        Assert.Equal(4.25, ChannelScaling.Identity.Apply(4.25));
        Assert.Null(ChannelScaling.Identity.Unit);
        Assert.True(ChannelScaling.Identity.IsIdentity);
    }

    [Fact]
    public void IsIdentity_IgnoresTheUnitLabel()
    {
        // A unit is a statement about what the number means, not a transform of it. The capability
        // document relies on this: it attaches "V" without changing a single reading.
        Assert.True(new ChannelScaling(1.0, 0.0, "V").IsIdentity);
        Assert.False(new ChannelScaling(2.0, 0.0, "V").IsIdentity);
        Assert.False(new ChannelScaling(1.0, 0.5, "V").IsIdentity);
    }

    [Fact]
    public void WithUnit_KeepsTheCoefficients()
    {
        var scaling = new ChannelScaling(gain: 20.0, offset: 3.0, unit: "V").WithUnit("PSI");

        Assert.Equal(20.0, scaling.Gain);
        Assert.Equal(3.0, scaling.Offset);
        Assert.Equal("PSI", scaling.Unit);
    }

    [Fact]
    public void WithUnit_WithTheSameUnit_ReturnsTheSameInstance()
    {
        // Every capability refresh runs this path over every channel; re-deriving an identical
        // instance each time would churn allocations for no change.
        var scaling = new ChannelScaling(1.0, 0.0, "V");

        Assert.Same(scaling, scaling.WithUnit("V"));
        Assert.Same(scaling, scaling.WithUnit(" V "));
    }

    [Fact]
    public void WithUnit_DoesNotMutateTheOriginal()
    {
        var original = ChannelScaling.Identity;

        original.WithUnit("PSI");

        Assert.Null(original.Unit);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new ChannelScaling(2.0, 1.0, "PSI"), new ChannelScaling(2.0, 1.0, "PSI"));
        Assert.NotEqual(new ChannelScaling(2.0, 1.0, "PSI"), new ChannelScaling(2.0, 1.0, "V"));
    }
}
