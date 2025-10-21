using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.Channel;

public class DigitalChannelTests
{
    [Fact]
    public void Constructor_WithValidChannelNumber_InitializesCorrectly()
    {
        // Arrange & Act
        var channel = new DigitalChannel(channelNumber: 5);

        // Assert
        Assert.Equal(5, channel.ChannelNumber);
        Assert.Equal("Digital Channel 5", channel.Name);
        Assert.Equal(ChannelType.Digital, channel.Type);
        Assert.Equal(ChannelDirection.Input, channel.Direction);
        Assert.False(channel.IsEnabled);
        Assert.False(channel.OutputValue);
    }

    [Fact]
    public void Constructor_WithNegativeChannelNumber_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new DigitalChannel(channelNumber: -1));
    }

    [Fact]
    public void OutputValue_CanBeSet()
    {
        // Arrange
        var channel = new DigitalChannel(0);

        // Act
        channel.OutputValue = true;

        // Assert
        Assert.True(channel.OutputValue);
    }

    [Fact]
    public void IsHigh_ReturnsTrue_WhenValueGreaterThanHalf()
    {
        // Arrange
        var channel = new DigitalChannel(0);
        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(1.0, timestamp);

        // Assert
        Assert.True(channel.IsHigh);
    }

    [Fact]
    public void IsHigh_ReturnsFalse_WhenValueLessThanHalf()
    {
        // Arrange
        var channel = new DigitalChannel(0);
        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(0.0, timestamp);

        // Assert
        Assert.False(channel.IsHigh);
    }

    [Fact]
    public void IsHigh_ReturnsFalse_WhenNoSampleSet()
    {
        // Arrange
        var channel = new DigitalChannel(0);

        // Act & Assert
        Assert.False(channel.IsHigh);
    }

    [Fact]
    public void SetActiveSample_UpdatesActiveSample()
    {
        // Arrange
        var channel = new DigitalChannel(0);
        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(1.0, timestamp);

        // Assert
        Assert.NotNull(channel.ActiveSample);
        Assert.Equal(1.0, channel.ActiveSample.Value);
        Assert.Equal(timestamp, channel.ActiveSample.Timestamp);
    }

    [Fact]
    public void SetActiveSample_RaisesSampleReceivedEvent()
    {
        // Arrange
        var channel = new DigitalChannel(0);
        var eventRaised = false;
        IDataSample? receivedSample = null;

        channel.SampleReceived += (sender, args) =>
        {
            eventRaised = true;
            receivedSample = args.Sample;
        };

        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(1.0, timestamp);

        // Assert
        Assert.True(eventRaised);
        Assert.NotNull(receivedSample);
        Assert.Equal(1.0, receivedSample.Value);
        Assert.Equal(timestamp, receivedSample.Timestamp);
    }

    [Fact]
    public void SetActiveSample_IsThreadSafe()
    {
        // Arrange
        var channel = new DigitalChannel(0);
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            var value = i % 2;
            tasks.Add(Task.Run(() => channel.SetActiveSample(value, DateTime.UtcNow)));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.NotNull(channel.ActiveSample);
    }

    [Fact]
    public void Properties_CanBeModified()
    {
        // Arrange
        var channel = new DigitalChannel(0);

        // Act
        channel.Name = "DIO 1";
        channel.IsEnabled = true;
        channel.Direction = ChannelDirection.Output;
        channel.OutputValue = true;

        // Assert
        Assert.Equal("DIO 1", channel.Name);
        Assert.True(channel.IsEnabled);
        Assert.Equal(ChannelDirection.Output, channel.Direction);
        Assert.True(channel.OutputValue);
    }

    [Fact]
    public void ToString_ReturnsChannelName()
    {
        // Arrange
        var channel = new DigitalChannel(0)
        {
            Name = "Test Digital Channel"
        };

        // Act
        var result = channel.ToString();

        // Assert
        Assert.Equal("Test Digital Channel", result);
    }
}
