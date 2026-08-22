using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct unit tests for <see cref="SdCardTickDelta"/> — the tick-delta-with-rollover
/// calculation previously duplicated identically across the CSV, JSON, and binary/protobuf
/// SD card log parsers.
/// </summary>
public class SdCardTickDeltaTests
{
    [Fact]
    public void Compute_WithNoRollover_ReturnsSimpleDifference()
    {
        var delta = SdCardTickDelta.Compute(previous: 100u, current: 150u);

        Assert.Equal(50L, delta);
    }

    [Fact]
    public void Compute_WithEqualTicks_ReturnsZero()
    {
        var delta = SdCardTickDelta.Compute(previous: 42u, current: 42u);

        Assert.Equal(0L, delta);
    }

    [Fact]
    public void Compute_WithSingleRollover_ReturnsWrappedDistance()
    {
        // previous is 10 ticks from uint.MaxValue; current is 5 ticks past the wrap.
        var previous = uint.MaxValue - 10;
        var current = 5u;

        var delta = SdCardTickDelta.Compute(previous, current);

        // 10 ticks remaining to the wrap + 1 (the wrap itself) + 5 ticks past it.
        Assert.Equal(16L, delta);
    }

    [Fact]
    public void Compute_WithCurrentAtMaxValue_ReturnsDistanceToMax()
    {
        var delta = SdCardTickDelta.Compute(previous: uint.MaxValue - 3, current: uint.MaxValue);

        Assert.Equal(3L, delta);
    }

    [Fact]
    public void Compute_WithPreviousAtZeroAndRollover_ReturnsCurrentPlusOne()
    {
        // previous == 0 is not "at max", so current < previous never happens here except when
        // current itself wrapped past zero — covered by the general rollover case above. This
        // instead exercises the boundary where previous is the last possible value before max.
        var delta = SdCardTickDelta.Compute(previous: uint.MaxValue, current: 0u);

        Assert.Equal(1L, delta);
    }
}
