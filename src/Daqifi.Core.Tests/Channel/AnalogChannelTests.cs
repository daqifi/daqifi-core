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
    public void Constructor_DefaultsResolutionIsAssumedToFalse()
    {
        // Arrange & Act
        var channel = new AnalogChannel(channelNumber: 0, resolution: 65535);

        // Assert
        Assert.False(channel.ResolutionIsAssumed);
    }

    [Fact]
    public void Constructor_WithResolutionIsAssumedTrue_SetsResolutionIsAssumed()
    {
        // Arrange & Act
        var channel = new AnalogChannel(channelNumber: 0, resolution: 65535, resolutionIsAssumed: true);

        // Assert
        Assert.True(channel.ResolutionIsAssumed);
    }

    [Theory]
    [InlineData(4095u)]   // 12-bit
    [InlineData(262143u)] // 18-bit (AD7609, e.g. Nyquist 3)
    [InlineData(16777215u)] // 24-bit
    public void Constructor_WithVariousBitDepthResolutions_InitializesCorrectly(uint resolution)
    {
        // Arrange & Act
        var channel = new AnalogChannel(channelNumber: 0, resolution: resolution);

        // Assert
        Assert.Equal(resolution, channel.Resolution);
    }

    [Theory]
    [InlineData(4095u)]   // 12-bit
    [InlineData(262143u)] // 18-bit (AD7609, e.g. Nyquist 3)
    [InlineData(16777215u)] // 24-bit
    public void GetScaledValue_WithVariousBitDepthResolutions_ScalesToFullRange(uint resolution)
    {
        // Arrange
        var channel = new AnalogChannel(0, resolution)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = 0.0,
            InternalScaleM = 1.0
        };

        // Act
        var result = channel.GetScaledValue((int)resolution);

        // Assert - the formula is bit-depth agnostic, so max raw value always scales to PortRange.
        Assert.Equal(10.0, result, precision: 6);
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
    public void SetActiveSample_EventArgsCarryRaisingChannel()
    {
        // Arrange
        var channel = new AnalogChannel(3);
        IChannel? eventChannel = null;
        channel.SampleReceived += (_, args) => eventChannel = args.Channel;

        // Act
        channel.SetActiveSample(1.0, DateTime.UtcNow);

        // Assert
        Assert.Same(channel, eventChannel);
    }

    [Fact]
    public void SetActiveSample_WithFullSample_PreservesRawValueAndDeviceTimestamp()
    {
        // Arrange
        var channel = new AnalogChannel(0);
        IDataSample? received = null;
        channel.SampleReceived += (_, args) => received = args.Sample;
        var sample = new DataSample(DateTime.UtcNow, 2.5, rawValue: 128, deviceTimestamp: 555u);

        // Act
        channel.SetActiveSample(sample);

        // Assert
        Assert.Same(sample, channel.ActiveSample);
        Assert.Equal(128, received!.RawValue);
        Assert.Equal(555u, received.DeviceTimestamp);
    }

    [Fact]
    public async Task SetActiveSample_IsThreadSafe()
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

        await Task.WhenAll(tasks.ToArray());

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

    // ---------------------------------------------------------------------
    // Bounds validation (daqifi-core#300)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(254u)]        // just below the 8-bit max-count floor
    [InlineData(16_777_217u)] // just above the 24-bit max-count ceiling
    public void Constructor_WithOutOfRangeResolution_ThrowsException(uint resolution)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogChannel(channelNumber: 0, resolution: resolution));
    }

    [Theory]
    [InlineData(255u)]         // 8-bit max-count floor
    [InlineData(16_777_216u)]  // 24-bit ceiling
    public void Constructor_WithBoundaryResolution_IsAccepted(uint resolution)
    {
        var channel = new AnalogChannel(channelNumber: 0, resolution: resolution);
        Assert.Equal(resolution, channel.Resolution);
    }

    [Theory]
    [InlineData(0.0)]                        // zero range
    [InlineData(-5.0)]                       // negative range
    [InlineData(AnalogChannel.MaxPortRangeVolts + 0.1)] // beyond max
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PortRange_WithInvalidValue_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.PortRange = value);
    }

    [Theory]
    [InlineData(0.0)]  // zero scale factor discards the measurement
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(AnalogChannel.MaxCalibrationMagnitude * 2)]
    public void CalibrationM_WithInvalidValue_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.CalibrationM = value);
    }

    [Fact]
    public void CalibrationM_WithNegativeValue_IsAccepted()
    {
        // A negative slope legitimately inverts the signal (e.g. reversed wiring).
        var channel = new AnalogChannel(0) { CalibrationM = -2.5 };
        Assert.Equal(-2.5, channel.CalibrationM);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(AnalogChannel.MaxCalibrationMagnitude * 2)]
    public void CalibrationB_WithInvalidValue_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.CalibrationB = value);
    }

    [Fact]
    public void CalibrationB_WithZero_IsAccepted()
    {
        // Zero is a valid offset (it's the default).
        var channel = new AnalogChannel(0) { CalibrationB = 0.0 };
        Assert.Equal(0.0, channel.CalibrationB);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InternalScaleM_WithInvalidValue_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.InternalScaleM = value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void MinValue_WithNonFinite_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.MinValue = value);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MaxValue_WithNonFinite_ThrowsException(double value)
    {
        var channel = new AnalogChannel(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.MaxValue = value);
    }

    [Fact]
    public void PortRange_AtMaxBoundary_IsAccepted()
    {
        var channel = new AnalogChannel(0) { PortRange = AnalogChannel.MaxPortRangeVolts };
        Assert.Equal(AnalogChannel.MaxPortRangeVolts, channel.PortRange);
    }

    // ---------------------------------------------------------------------
    // Bipolar / signed scaling (daqifi-core#297)
    // ---------------------------------------------------------------------

    [Fact]
    public void GetScaledValue_WithNegativeRawValue_ProducesNegativeVoltage()
    {
        // ±10V bipolar range: signed two's-complement raw counts should map straight through
        // to signed voltages with no unipolar-only assumption in the formula.
        var channel = new AnalogChannel(0, 262143)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = 0.0,
            InternalScaleM = 1.0,
            MinValue = -10.0,
            MaxValue = 10.0
        };

        // -full scale -> -PortRange
        Assert.Equal(-10.0, channel.GetScaledValue(-262143), precision: 6);
        // -half scale -> -PortRange/2
        Assert.Equal(-5.0, channel.GetScaledValue(-131072), precision: 2);
        // zero raw -> 0 V (no offset)
        Assert.Equal(0.0, channel.GetScaledValue(0), precision: 6);
    }

    [Fact]
    public void GetScaledValue_IsSymmetricAboutZeroForBipolarRange()
    {
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 5.0,
            CalibrationM = 1.0,
            CalibrationB = 0.0,
            InternalScaleM = 1.0
        };

        var positive = channel.GetScaledValue(20000);
        var negative = channel.GetScaledValue(-20000);

        Assert.Equal(-positive, negative, precision: 9);
    }

    [Fact]
    public void GetScaledValue_WithNegativeRawAndOffset_AppliesOffsetAfterSignedGain()
    {
        // Formula: (raw/Res * PortRange * M + B) * InternalScaleM.
        // At -full scale with M=1, B=1: (-1 * 10 * 1 + 1) = -9.
        var channel = new AnalogChannel(0, 262143)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = 1.0,
            InternalScaleM = 1.0
        };

        Assert.Equal(-9.0, channel.GetScaledValue(-262143), precision: 6);
    }

    [Fact]
    public void GetScaledValue_WithNegativeCalibrationM_InvertsSign()
    {
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 10.0,
            CalibrationM = -1.0,
            CalibrationB = 0.0,
            InternalScaleM = 1.0
        };

        // A negative raw with an inverting slope yields a positive voltage.
        Assert.Equal(10.0, channel.GetScaledValue(-65535), precision: 6);
    }

    [Fact]
    public void IsBipolar_ReflectsConfiguredMinValue()
    {
        var bipolar = new AnalogChannel(0) { MinValue = -10.0, MaxValue = 10.0 };
        Assert.True(bipolar.IsBipolar);

        var unipolar = new AnalogChannel(0) { MinValue = 0.0, MaxValue = 10.0 };
        Assert.False(unipolar.IsBipolar);
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
