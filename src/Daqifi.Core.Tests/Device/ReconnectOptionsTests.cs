using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// The reconnect policy itself (issue #379): its defaults, its backoff arithmetic, and the values
/// it refuses. A policy that accepted "zero attempts" or a negative delay would turn into a device
/// that quietly never reconnects, or a loop that never waits.
/// </summary>
public class ReconnectOptionsTests
{
    [Fact]
    public void ANewPolicyIsOff()
    {
        var options = new ReconnectOptions();

        Assert.False(options.Enabled);
        Assert.True(options.ResumeStreaming);
        Assert.Equal(5, options.MaxAttempts);
    }

    [Fact]
    public void ThePresetsSayWhatTheyMean()
    {
        Assert.False(ReconnectOptions.Disabled.Enabled);
        Assert.True(ReconnectOptions.Default.Enabled);
        Assert.True(ReconnectOptions.Fast.Enabled);
        Assert.True(ReconnectOptions.Resilient.Enabled);

        // Resilient keeps trying for far longer than Fast does.
        Assert.True(ReconnectOptions.Resilient.MaxAttempts > ReconnectOptions.Fast.MaxAttempts);
        Assert.True(ReconnectOptions.Resilient.MaxDelay > ReconnectOptions.Fast.MaxDelay);
    }

    [Fact]
    public void TheFirstAttemptStillWaits()
    {
        // Unlike the initial-connect retry policy: at the instant a drop is detected the endpoint is
        // gone by definition, so trying immediately is guaranteed to fail.
        var options = new ReconnectOptions { InitialDelay = TimeSpan.FromSeconds(2) };

        Assert.Equal(TimeSpan.FromSeconds(2), options.CalculateDelay(1));
    }

    [Fact]
    public void TheDelayBacksOffAndThenStopsGrowing()
    {
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(1), options.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), options.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), options.CalculateDelay(3));

        // Capped from here on, however far the attempt count runs.
        Assert.Equal(TimeSpan.FromSeconds(5), options.CalculateDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(5), options.CalculateDelay(50));
        Assert.Equal(TimeSpan.FromSeconds(5), options.CalculateDelay(10_000));
    }

    [Fact]
    public void AMultiplierOfOneGivesAFixedDelay()
    {
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.FromMilliseconds(750),
            BackoffMultiplier = 1.0
        };

        Assert.Equal(TimeSpan.FromMilliseconds(750), options.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.CalculateDelay(9));
    }

    [Fact]
    public void AZeroInitialDelayStaysZero()
    {
        var options = new ReconnectOptions { InitialDelay = TimeSpan.Zero };

        Assert.Equal(TimeSpan.Zero, options.CalculateDelay(1));
        Assert.Equal(TimeSpan.Zero, options.CalculateDelay(7));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(1075)]
    [InlineData(4096)]
    [InlineData(int.MaxValue)]
    public void AZeroInitialDelayStaysZero_EvenWhereTheBackoffFactorOverflows(int attemptNumber)
    {
        // Regression: the exponential factor overflows to +Infinity past roughly attempt 1075 at a
        // multiplier of 2, and 0 x Infinity is NaN. That NaN used to be answered with MaxDelay, so a
        // policy configured for immediate retries silently became a 30-second wait — the opposite of
        // what it asked for.
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.Zero,
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(TimeSpan.Zero, options.CalculateDelay(attemptNumber));
    }

    [Fact]
    public void AnOverflowingBackoffOnARealDelay_SettlesAtTheCap()
    {
        // The other side of the same overflow: with a positive InitialDelay the product is
        // +Infinity rather than NaN, and the cap is the right answer.
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(TimeSpan.FromSeconds(30), options.CalculateDelay(2000));
        Assert.Equal(TimeSpan.FromSeconds(30), options.CalculateDelay(int.MaxValue));
    }

    [Fact]
    public void MaxDelayCapsTheFirstAttemptToo()
    {
        // A ceiling that exempted the very first wait would not be a ceiling.
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(2)
        };

        Assert.Equal(TimeSpan.FromSeconds(2), options.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), options.CalculateDelay(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void APolicyThatWouldNeverTry_IsRejected(int maxAttempts)
    {
        var options = new ReconnectOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxAttempts = maxAttempts);

        // Pinned in full — the guard's job is to name the property the caller actually wrote and
        // hand back the value it rejected, not merely to throw something. The sibling policy
        // Communication.Transport.ConnectionRetryOptions is already held to this standard.
        Assert.Equal(nameof(ReconnectOptions.MaxAttempts), ex.ParamName);
        Assert.Equal(maxAttempts, ex.ActualValue);
        Assert.StartsWith("At least one reconnect attempt must be allowed.", ex.Message);
        Assert.Equal(5, options.MaxAttempts); // unchanged by the rejected write
    }

    [Fact]
    public void NegativeDelaysAreRejected()
    {
        var options = new ReconnectOptions();

        var initial = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.InitialDelay = TimeSpan.FromSeconds(-1));
        var max = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.MaxDelay = TimeSpan.FromSeconds(-1));

        Assert.Equal(nameof(ReconnectOptions.InitialDelay), initial.ParamName);
        Assert.Equal(TimeSpan.FromSeconds(-1), initial.ActualValue);
        Assert.StartsWith("The delay cannot be negative.", initial.Message);

        Assert.Equal(nameof(ReconnectOptions.MaxDelay), max.ParamName);
        Assert.Equal(TimeSpan.FromSeconds(-1), max.ActualValue);
        Assert.StartsWith("The delay cannot be negative.", max.Message);

        // Both writes were refused outright, not clamped to zero.
        Assert.Equal(TimeSpan.FromSeconds(1), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxDelay);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void ABackoffThatShrinksOrIsNotANumber_IsRejected(double multiplier)
    {
        var options = new ReconnectOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.BackoffMultiplier = multiplier);

        Assert.Equal(nameof(ReconnectOptions.BackoffMultiplier), ex.ParamName);
        Assert.Equal(multiplier, ex.ActualValue);
        Assert.StartsWith("The backoff multiplier must be at least 1.0.", ex.Message);
        Assert.Equal(2.0, options.BackoffMultiplier); // unchanged by the rejected write
    }

    [Fact]
    public void ThePresetPoliciesSatisfyTheirOwnGuards()
    {
        // Every preset is built through the same validating setters, so a preset that drifted
        // outside the accepted range would throw before these assertions ever ran.
        foreach (var options in new[]
                 {
                     ReconnectOptions.Disabled, ReconnectOptions.Default,
                     ReconnectOptions.Fast, ReconnectOptions.Resilient
                 })
        {
            Assert.True(options.MaxAttempts >= 1);
            Assert.True(options.InitialDelay >= TimeSpan.Zero);
            Assert.True(options.MaxDelay >= TimeSpan.Zero);
            Assert.True(options.BackoffMultiplier >= 1.0);
        }
    }
}
