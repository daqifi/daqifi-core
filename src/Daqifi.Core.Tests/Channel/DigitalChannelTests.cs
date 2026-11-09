using System.Collections.Concurrent;
using Daqifi.Core.Channel;

// IDigitalChannel.OutputValue is obsolete (#901); these tests keep covering it until it
// is removed in a major version.
#pragma warning disable CS0618

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
    public async Task SetActiveSample_IsThreadSafe()
    {
        var channel = new DigitalChannel(0);
        var received = new ConcurrentBag<IDataSample>();
        channel.SampleReceived += (_, args) => received.Add(args.Sample);

        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var value = i % 2;
            tasks.Add(Task.Run(() => channel.SetActiveSample(value, DateTime.UtcNow)));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(100, received.Count);
        Assert.Contains(received, s => ReferenceEquals(s, channel.ActiveSample));
        Assert.All(received, s => Assert.True(s.Value is 0.0 or 1.0));
    }

    [Fact]
    public void Name_Null_Throws()
    {
        var channel = new DigitalChannel(0);

        var ex = Assert.Throws<ArgumentNullException>(() => channel.Name = null!);
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void SetActiveSample_NullSample_Throws()
    {
        var channel = new DigitalChannel(0);

        Assert.Throws<ArgumentNullException>(() => channel.SetActiveSample(null!));
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

    [Fact]
    public void Mode_FollowsTheTransitionsOfThePin()
    {
        var channel = new DigitalChannel(0, isPwmCapable: true);
        Assert.Equal(DigitalPinMode.Input, channel.Mode);

        channel.ApplyDirection(ChannelDirection.Output);
        Assert.Equal(DigitalPinMode.Output, channel.Mode);

        channel.EnterPwm();
        Assert.Equal(DigitalPinMode.Pwm, channel.Mode);
        Assert.True(channel.IsPwmEnabled);

        channel.ExitPwm();
        Assert.Equal(DigitalPinMode.Output, channel.Mode);
        Assert.False(channel.IsPwmEnabled);

        channel.ApplyDirection(ChannelDirection.Input);
        Assert.Equal(DigitalPinMode.Input, channel.Mode);
    }

    [Fact]
    public void EnterPwm_FromInput_ReadsAsOutputAndReturnsToInput()
    {
        // The PWM timer drives the pin, so the channel never shows PWM enabled with direction
        // Input; the stored direction comes back when PWM stops, as it does on the device.
        var channel = new DigitalChannel(0, isPwmCapable: true);

        channel.EnterPwm();
        Assert.Equal(ChannelDirection.Output, channel.Direction);

        // A status message that reports the stored direction does not change that.
        channel.ApplyDirection(ChannelDirection.Input);
        Assert.Equal(DigitalPinMode.Pwm, channel.Mode);
        Assert.Equal(ChannelDirection.Output, channel.Direction);

        channel.ExitPwm();
        Assert.Equal(ChannelDirection.Input, channel.Direction);
        Assert.Equal(DigitalPinMode.Input, channel.Mode);
    }

    [Fact]
    public void EnterPwm_OnAChannelThatCannotDoPwm_Throws()
    {
        var channel = new DigitalChannel(2, isPwmCapable: false);

        Assert.Throws<InvalidOperationException>(() => channel.EnterPwm());

        Assert.False(channel.IsPwmEnabled);
        Assert.Equal(DigitalPinMode.Input, channel.Mode);
    }

    [Fact]
    public void ApplyPwmCapability_False_LeavesPwmMode()
    {
        var channel = new DigitalChannel(0, isPwmCapable: true);
        channel.EnterPwm();

        channel.ApplyPwmCapability(false);

        Assert.False(channel.IsPwmCapable);
        Assert.False(channel.IsPwmEnabled);
        Assert.Equal(DigitalPinMode.Input, channel.Mode);
    }

    [Fact]
    public void ExitPwm_ClearsTheRecordedOutputLevel()
    {
        var channel = new DigitalChannel(0, isPwmCapable: true);
        channel.ApplyDirection(ChannelDirection.Output);
        channel.ApplyOutputValue(true);
        channel.EnterPwm();

        channel.ExitPwm();

        Assert.False(channel.OutputValue);
    }

    [Theory]
    [InlineData(nameof(DigitalChannel.Direction), typeof(DigitalChannel))]
    [InlineData(nameof(DigitalChannel.IsPwmEnabled), typeof(DigitalChannel))]
    [InlineData(nameof(IDigitalChannel.IsPwmEnabled), typeof(IDigitalChannel))]
    public void PublicModeSetters_AreObsoleteAndNameTheReplacement(string property, Type type)
    {
        var setter = type.GetProperty(property)!.SetMethod!;

        var obsolete = Assert.Single(setter.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false));
        Assert.Contains("IStreamingDevice.Set", ((ObsoleteAttribute)obsolete).Message);
    }

    [Fact]
    public void Mode_DefaultInterfaceImplementation_DerivesFromThePublicState()
    {
        IDigitalChannel channel = new ForeignDigitalChannel { Direction = ChannelDirection.Output };
        Assert.Equal(DigitalPinMode.Output, channel.Mode);

        channel.IsPwmEnabled = true;
        Assert.Equal(DigitalPinMode.Pwm, channel.Mode);
    }

    /// <summary>
    /// An implementation written before <see cref="IDigitalChannel.Mode"/> existed: it does not
    /// declare the property and must still compile and report a mode.
    /// </summary>
    private sealed class ForeignDigitalChannel : IDigitalChannel
    {
        public int ChannelNumber => 0;
        public string Name { get; set; } = "foreign";
        public bool IsEnabled { get; set; }
        public ChannelType Type => ChannelType.Digital;
        public ChannelDirection Direction { get; set; }
        public IDataSample? ActiveSample => null;
        public bool OutputValue { get; set; }
        public bool IsHigh => false;
        public bool IsPwmCapable => true;
        public bool IsPwmEnabled { get; set; }
        public int PwmDutyCyclePercent { get; set; }

        public event EventHandler<SampleReceivedEventArgs>? SampleReceived { add { } remove { } }

        public void SetActiveSample(double value, DateTime timestamp) { }

        public void SetActiveSample(IDataSample sample) { }
    }
}
