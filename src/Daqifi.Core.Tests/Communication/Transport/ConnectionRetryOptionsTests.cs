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
}
