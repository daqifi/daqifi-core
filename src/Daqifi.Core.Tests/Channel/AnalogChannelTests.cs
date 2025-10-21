using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.Channel;

public class AnalogChannelTests
{
    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        // Arrange & Act
        var channel = new AnalogChannel(channelNumber: 0, resolution: 65535);

        // Assert
        Assert.Equal(0, channel.ChannelNumber);
        Assert.Equal(65535u, channel.Resolution);
        Assert.Equal("Analog Channel 0", channel.Name);
        Assert.Equal(ChannelType.Analog, channel.Type);
        Assert.Equal(ChannelDirection.Input, channel.Direction);
        Assert.False(channel.IsEnabled);
        Assert.Equal(1.0, channel.CalibrationM);
        Assert.Equal(0.0, channel.CalibrationB);
        Assert.Equal(1.0, channel.InternalScaleM);
        Assert.Equal(1.0, channel.PortRange);
    }

    [Fact]
    public void Constructor_WithNegativeChannelNumber_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogChannel(channelNumber: -1));
    }

    [Fact]
    public void Constructor_WithZeroResolution_ThrowsException()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogChannel(channelNumber: 0, resolution: 0));
    }

    [Fact]
    public void GetScaledValue_WithDefaultCalibration_ScalesCorrectly()
    {
        // Arrange
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = 0.0,
            InternalScaleM = 1.0
        };

        // Act
        var result = channel.GetScaledValue(32767); // Half of resolution

        // Assert
        // Expected: (32767 / 65535) * 10.0 * 1.0 + 0.0) * 1.0 ≈ 5.0
        Assert.Equal(5.0, result, precision: 2);
    }

    [Fact]
    public void GetScaledValue_WithCalibration_AppliesCorrectly()
    {
        // Arrange
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 1.0,
            CalibrationM = 2.0,
            CalibrationB = 1.0,
            InternalScaleM = 1.0
        };

        // Act
        var result = channel.GetScaledValue(65535); // Max value

        // Assert
        // Expected: (65535 / 65535) * 1.0 * 2.0 + 1.0 = 3.0
        Assert.Equal(3.0, result, precision: 6);
    }

    [Fact]
    public void GetScaledValue_WithInternalScale_AppliesCorrectly()
    {
        // Arrange
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 1.0,
            CalibrationM = 1.0,
            CalibrationB = 0.0,
            InternalScaleM = 10.0
        };

        // Act
        var result = channel.GetScaledValue(32767); // Half value

        // Assert
        // Expected: ((32767 / 65535) * 1.0 * 1.0 + 0.0) * 10.0 ≈ 5.0
        Assert.Equal(5.0, result, precision: 2);
    }

    [Fact]
    public void SetActiveSample_UpdatesActiveSample()
    {
        // Arrange
        var channel = new AnalogChannel(0);
        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(42.5, timestamp);

        // Assert
        Assert.NotNull(channel.ActiveSample);
        Assert.Equal(42.5, channel.ActiveSample.Value);
        Assert.Equal(timestamp, channel.ActiveSample.Timestamp);
    }

    [Fact]
    public void SetActiveSample_RaisesSampleReceivedEvent()
    {
        // Arrange
        var channel = new AnalogChannel(0);
        var eventRaised = false;
        IDataSample? receivedSample = null;

        channel.SampleReceived += (sender, args) =>
        {
            eventRaised = true;
            receivedSample = args.Sample;
        };

        var timestamp = DateTime.UtcNow;

        // Act
        channel.SetActiveSample(42.5, timestamp);

        // Assert
        Assert.True(eventRaised);
        Assert.NotNull(receivedSample);
        Assert.Equal(42.5, receivedSample.Value);
        Assert.Equal(timestamp, receivedSample.Timestamp);
    }

    [Fact]
    public void SetActiveSample_IsThreadSafe()
    {
        // Arrange
        var channel = new AnalogChannel(0);
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            var value = i;
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
        var channel = new AnalogChannel(0);

        // Act
        channel.Name = "Temperature";
        channel.IsEnabled = true;
        channel.Direction = ChannelDirection.Output;
        channel.MinValue = -100.0;
        channel.MaxValue = 100.0;
        channel.CalibrationM = 2.0;
        channel.CalibrationB = 1.5;
        channel.InternalScaleM = 0.5;
        channel.PortRange = 5.0;

        // Assert
        Assert.Equal("Temperature", channel.Name);
        Assert.True(channel.IsEnabled);
        Assert.Equal(ChannelDirection.Output, channel.Direction);
        Assert.Equal(-100.0, channel.MinValue);
        Assert.Equal(100.0, channel.MaxValue);
        Assert.Equal(2.0, channel.CalibrationM);
        Assert.Equal(1.5, channel.CalibrationB);
        Assert.Equal(0.5, channel.InternalScaleM);
        Assert.Equal(5.0, channel.PortRange);
    }

    [Fact]
    public void ToString_ReturnsChannelName()
    {
        // Arrange
        var channel = new AnalogChannel(0)
        {
            Name = "Test Channel"
        };

        // Act
        var result = channel.ToString();

        // Assert
        Assert.Equal("Test Channel", result);
    }
}
