using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class ConnectionRetryOptionsTests
{
    [Fact]
    public void Constructor_ShouldSetDefaultValues()
    {
        // Act
        var options = new ConnectionRetryOptions();

        // Assert
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
        // Act
        var options = ConnectionRetryOptions.NoRetry;

        // Assert
        Assert.False(options.Enabled);
        Assert.Equal(1, options.MaxAttempts);
    }

    [Fact]
    public void Fast_ShouldCreateFastReconnectOptions()
    {
        // Act
        var options = ConnectionRetryOptions.Fast;

        // Assert
        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), options.MaxDelay);
        Assert.Equal(1.5, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(3), options.ConnectionTimeout);
    }

    [Fact]
    public void Resilient_ShouldCreateResilientOptions()
    {
        // Act
        var options = ConnectionRetryOptions.Resilient;

        // Assert
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxDelay);
        Assert.Equal(2.5, options.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectionTimeout);
    }

    [Fact]
    public void CalculateDelay_FirstAttempt_ShouldReturnZero()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var delay = options.CalculateDelay(1);

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void CalculateDelay_SecondAttempt_ShouldReturnInitialDelay()
    {
        // Arrange
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0
        };

        // Act
        var delay = options.CalculateDelay(2);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void CalculateDelay_ThirdAttempt_ShouldApplyExponentialBackoff()
    {
        // Arrange
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        // Act
        var delay = options.CalculateDelay(3);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(2), delay); // 1 * 2^1 = 2
    }

    [Fact]
    public void CalculateDelay_FourthAttempt_ShouldApplyExponentialBackoff()
    {
        // Arrange
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        // Act
        var delay = options.CalculateDelay(4);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(4), delay); // 1 * 2^2 = 4
    }

    [Fact]
    public void CalculateDelay_ShouldRespectMaxDelay()
    {
        // Arrange
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(15)
        };

        // Act
        var delay = options.CalculateDelay(5); // Would be 10 * 2^3 = 80 seconds

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(15), delay); // Capped at MaxDelay
    }

    [Fact]
    public void CalculateDelay_WithCustomMultiplier_ShouldWork()
    {
        // Arrange
        var options = new ConnectionRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 1.5,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        // Act
        var delay2 = options.CalculateDelay(2);
        var delay3 = options.CalculateDelay(3);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), delay2); // 1 * 1.5^0 = 1
        Assert.Equal(TimeSpan.FromMilliseconds(1500), delay3); // 1 * 1.5^1 = 1.5
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MaxAttempts_BelowOne_ShouldThrowNamingTheProperty(int value)
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxAttempts = value);

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.MaxAttempts), ex.ParamName);
        Assert.Equal(3, options.MaxAttempts); // unchanged
    }

    [Fact]
    public void InitialDelay_Negative_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.InitialDelay = TimeSpan.FromMilliseconds(-1));

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.InitialDelay), ex.ParamName);
    }

    [Fact]
    public void InitialDelay_Zero_ShouldBeAccepted()
    {
        // Arrange & Act — zero means "retry immediately", which the executor supports.
        var options = new ConnectionRetryOptions { InitialDelay = TimeSpan.Zero };

        // Assert
        Assert.Equal(TimeSpan.Zero, options.InitialDelay);
        Assert.Equal(TimeSpan.Zero, options.CalculateDelay(2));
    }

    [Fact]
    public void MaxDelay_Negative_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.MaxDelay = TimeSpan.FromSeconds(-1));

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.MaxDelay), ex.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void BackoffMultiplier_BelowOne_ShouldThrowNamingTheProperty(double value)
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.BackoffMultiplier = value);

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.BackoffMultiplier), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_Zero_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act — the bench repro: the platform used to answer this with an
        // ArgumentOutOfRangeException naming SerialPort.WriteTimeout, after the full backoff.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.Zero);

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
        Assert.Contains("at least 1 millisecond", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectionTimeout); // unchanged
    }

    [Fact]
    public void ConnectionTimeout_Negative_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromSeconds(-1));

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_SubMillisecond_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act — positive, but both transports narrow the timeout to a millisecond int, where a
        // single tick truncates to 0 and lands back in the platform error this guard exists for.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromTicks(1));

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_AtOneMillisecond_ShouldBeAccepted()
    {
        // Arrange & Act — the smallest value that survives the narrowing intact.
        var options = new ConnectionRetryOptions { ConnectionTimeout = TimeSpan.FromMilliseconds(1) };

        // Assert
        Assert.Equal(1, (int)options.ConnectionTimeout.TotalMilliseconds);
    }

    [Fact]
    public void ConnectionTimeout_BeyondIntMaxMilliseconds_ShouldThrowNamingTheProperty()
    {
        // Arrange
        var options = new ConnectionRetryOptions();

        // Act — both transports narrow this to a millisecond int, so a longer span would
        // wrap round to a negative timeout and be rejected by the platform instead.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.ConnectionTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1));

        // Assert
        Assert.Equal(nameof(ConnectionRetryOptions.ConnectionTimeout), ex.ParamName);
    }

    [Fact]
    public void ConnectionTimeout_AtIntMaxMilliseconds_ShouldBeAccepted()
    {
        // Arrange & Act
        var options = new ConnectionRetryOptions
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(int.MaxValue)
        };

        // Assert
        Assert.Equal(int.MaxValue, (int)options.ConnectionTimeout.TotalMilliseconds);
    }

    [Fact]
    public void PresetPolicies_ShouldSatisfyTheirOwnGuards()
    {
        // Act & Assert — the presets go through the same setters, so this would throw
        // at construction if a guard and a preset ever disagreed.
        Assert.True(ConnectionRetryOptions.NoRetry.ConnectionTimeout > TimeSpan.Zero);
        Assert.True(ConnectionRetryOptions.Fast.ConnectionTimeout > TimeSpan.Zero);
        Assert.True(ConnectionRetryOptions.Resilient.ConnectionTimeout > TimeSpan.Zero);
    }
}
