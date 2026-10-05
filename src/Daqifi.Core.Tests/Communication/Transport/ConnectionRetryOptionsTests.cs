using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class ConnectionRetryOptionsTests
{
    [Fact]
    public void Constructor_ShouldSetDefaultValues()
    {
        var options = new ConnectionRetryOptions();

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxDelay);
        Assert.Equal(2.0, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectionTimeout);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void NoRetry_ShouldCreateDisabledOptions()
    {
        var options = ConnectionRetryOptions.NoRetry;

        Assert.False(options.Enabled);
        Assert.Equal(1, options.MaxAttempts);
    }

    [Fact]
    public void Fast_ShouldCreateFastReconnectOptions()
    {
        var options = ConnectionRetryOptions.Fast;

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), options.MaxDelay);
        Assert.Equal(1.5, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(3), options.ConnectionTimeout);
    }

    [Fact]
    public void Resilient_ShouldCreateResilientOptions()
    {
        var options = ConnectionRetryOptions.Resilient;

        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxDelay);
        Assert.Equal(2.5, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectionTimeout);
    }

    [Fact]
    public void CalculateDelay_FirstAttempt_ShouldReturnZero()
    {
        var options = new ConnectionRetryOptions();

        var delay = options.CalculateDelay(1);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    // delay = InitialDelay * BackoffMultiplier^(attempt-2). MaxDelay is 60s, above every row
    // (1s, 2s, 4s), so these pin the backoff. The cap is CalculateDelay_ShouldRespectMaxDelay.
    [Theory]
    [InlineData(2, 1)] // 1 * 2^0
    [InlineData(3, 2)] // 1 * 2^1
    [InlineData(4, 4)] // 1 * 2^2
    public void CalculateDelay_LaterAttempts_ShouldApplyExponentialBackoff(int attempt, int expectedSeconds)
    {
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.CalculateDelay(attempt));
    }

    [Fact]
    public void CalculateDelay_ShouldRespectMaxDelay()
    {
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(15)
        };

        var delay = options.CalculateDelay(5); // Would be 10 * 2^3 = 80 seconds

        Assert.Equal(TimeSpan.FromSeconds(15), delay); // Capped at MaxDelay
    }

    [Fact]
    public void CalculateDelay_WithCustomMultiplier_ShouldWork()
    {
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 1.5,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        var delay2 = options.CalculateDelay(2);
        var delay3 = options.CalculateDelay(3);

        Assert.Equal(TimeSpan.FromSeconds(1), delay2); // 1 * 1.5^0 = 1
        Assert.Equal(TimeSpan.FromMilliseconds(1500), delay3); // 1 * 1.5^1 = 1.5
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MaxAttempts_BelowOne_ShouldThrowNamingTheProperty(int value)
    {
        var options = new ConnectionRetryOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxAttempts = value);

        Assert.Equal(nameof(ConnectionRetryOptions.MaxAttempts), ex.ParamName);
        Assert.Equal(3, options.MaxAttempts); // unchanged
    }

    [Fact]
    public void InitialDelay_Negative_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.InitialDelay = TimeSpan.FromMilliseconds(-1));

        Assert.Equal(nameof(ConnectionRetryOptions.InitialDelay), ex.ParamName);
    }

    [Fact]
    public void InitialDelay_Zero_ShouldBeAccepted()
    {
        // Arrange & Act — zero means "retry immediately", which the executor supports.
        var options = new ConnectionRetryOptions { InitialDelay = TimeSpan.Zero };

        Assert.Equal(TimeSpan.Zero, options.InitialDelay);
        Assert.Equal(TimeSpan.Zero, options.CalculateDelay(2));
    }

    [Fact]
    public void MaxDelay_Negative_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.MaxDelay = TimeSpan.FromSeconds(-1));

        Assert.Equal(nameof(ConnectionRetryOptions.MaxDelay), ex.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void BackoffMultiplier_BelowOne_ShouldThrowNamingTheProperty(double value)
    {
        var options = new ConnectionRetryOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.BackoffMultiplier = value);

        Assert.Equal(nameof(ConnectionRetryOptions.BackoffMultiplier), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_Zero_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        // Act — the bench repro: the platform used to answer this with an
        // ArgumentOutOfRangeException naming SerialPort.WriteTimeout, after the full backoff.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.Zero);

        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
        Assert.Contains("at least 1 millisecond", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectionTimeout); // unchanged
    }

    [Fact]
    public void ConnectionTimeout_Negative_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromSeconds(-1));

        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_SubMillisecond_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        // Act — positive, but both transports narrow the timeout to a millisecond int, where a
        // single tick truncates to 0 and lands back in the platform error this guard exists for.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromTicks(1));

        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_AtOneMillisecond_ShouldBeAccepted()
    {
        // Arrange & Act — the smallest value that survives the narrowing intact.
        var options = new ConnectionRetryOptions { ConnectionTimeout = TimeSpan.FromMilliseconds(1) };

        Assert.Equal(1, (int)options.ConnectionTimeout.TotalMilliseconds);
    }

    [Fact]
    public void ConnectionTimeout_BeyondIntMaxMilliseconds_ShouldThrowNamingTheProperty()
    {
        var options = new ConnectionRetryOptions();

        // Act — both transports narrow this to a millisecond int, so a longer span would
        // wrap round to a negative timeout and be rejected by the platform instead.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1));

        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_AtIntMaxMilliseconds_ShouldBeAccepted()
    {
        var options = new ConnectionRetryOptions
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(int.MaxValue)
        };

        Assert.Equal(int.MaxValue, (int)options.ConnectionTimeout.TotalMilliseconds);
    }
}
