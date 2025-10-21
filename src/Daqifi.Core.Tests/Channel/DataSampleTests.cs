using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.Channel;

public class DataSampleTests
{
    [Fact]
    public void Constructor_WithNoParameters_SetsDefaultValues()
    {
        // Arrange & Act
        var sample = new DataSample();

        // Assert
        Assert.NotEqual(default, sample.Timestamp);
        Assert.Equal(0.0, sample.Value);
    }

    [Fact]
    public void Constructor_WithParameters_SetsProvidedValues()
    {
        // Arrange
        var timestamp = new DateTime(2025, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var value = 42.5;

        // Act
        var sample = new DataSample(timestamp, value);

        // Assert
        Assert.Equal(timestamp, sample.Timestamp);
        Assert.Equal(value, sample.Value);
    }

    [Fact]
    public void Value_CanBeModified()
    {
        // Arrange
        var sample = new DataSample(DateTime.UtcNow, 10.0);

        // Act
        sample.Value = 20.0;

        // Assert
        Assert.Equal(20.0, sample.Value);
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        // Arrange
        var timestamp = new DateTime(2025, 10, 20, 12, 30, 45, 123, DateTimeKind.Utc);
        var sample = new DataSample(timestamp, 42.5);

        // Act
        var result = sample.ToString();

        // Assert
        Assert.Contains("2025-10-20", result);
        Assert.Contains("12:30:45", result);
        Assert.Contains("42.5", result);
    }
}
