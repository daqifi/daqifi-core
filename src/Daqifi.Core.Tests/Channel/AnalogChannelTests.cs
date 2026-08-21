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
        // Expected: 32767 / 65535 * 10.0 * 1.0 * 1.0 + 0.0 ≈ 5.0
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
        // Expected: 65535 / 65535 * 1.0 * 2.0 * 1.0 + 1.0 = 3.0
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
        // Expected: 32767 / 65535 * 1.0 * 1.0 * 10.0 + 0.0 ≈ 5.0
        Assert.Equal(5.0, result, precision: 2);
    }

    [Fact]
    public void GetScaledValue_WithInternalScaleAndOffset_DoesNotScaleTheOffset()
    {
        // Regression pin for daqifi-core#387: CalibrationB is an offset in volts and must be added
        // after InternalScaleM is applied, matching the firmware's own conversion
        // (MC12bADC.c: (range * scale * calM * raw) / resolution + calB, behind MEAS:VOLT:DC?).
        // The old form multiplied CalibrationB by InternalScaleM, which diverged from the device
        // whenever InternalScaleM != 1 and CalibrationB != 0.
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = 0.25,
            InternalScaleM = 2.0
        };

        // Correct:  65535 / 65535 * 10.0 * 1.0 * 2.0 + 0.25 = 20.25
        // Old (bug): (65535 / 65535 * 10.0 * 1.0 + 0.25) * 2.0 = 20.50
        Assert.Equal(20.25, channel.GetScaledValue(65535), precision: 6);
    }

    [Fact]
    public void GetScaledValue_WithInternalScaleAndOffset_AtZeroRawReturnsTheOffsetUnscaled()
    {
        // At raw 0 the gain term vanishes, so the result is exactly CalibrationB — the cleanest
        // statement that the offset is never multiplied by InternalScaleM (daqifi-core#387).
        var channel = new AnalogChannel(0, 65535)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            CalibrationB = -0.05,
            InternalScaleM = 4.0
        };

        Assert.Equal(-0.05, channel.GetScaledValue(0), precision: 9);
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
        // Formula: raw/Res * PortRange * M * InternalScaleM + B.
        // At -full scale with M=1, InternalScaleM=1, B=1: (-1 * 10 * 1 * 1) + 1 = -9.
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

    #region Engineering units (#501)

    [Fact]
    public void Scaling_IsNullUntilSomethingStatesOne()
    {
        var channel = new AnalogChannel(0);

        Assert.Null(channel.Scaling);
        Assert.Null(((IScaledChannel)channel).Unit);
    }

    [Fact]
    public void Unit_IsShorthandForTheScalingsUnit()
    {
        IScaledChannel channel = new AnalogChannel(0) { Scaling = new ChannelScaling(2.0, 0.0, "PSI") };

        Assert.Equal("PSI", channel.Unit);
    }

    [Fact]
    public void SetActiveSample_StampsTheChannelsScalingOntoTheSample()
    {
        // The value overload builds the sample itself, so it is the only place that can attach the
        // scaling — a sample from here has to report engineering units exactly as a decoded one does.
        var channel = new AnalogChannel(0) { Scaling = new ChannelScaling(gain: 20.0, unit: "PSI") };

        channel.SetActiveSample(2.5, DateTime.UtcNow);

        Assert.Equal(2.5, channel.ActiveSample!.Value);
        Assert.Equal(50.0, channel.ActiveSample.ScaledValue);
        Assert.Equal("PSI", channel.ActiveSample.Unit);
    }

    [Fact]
    public void SetActiveSample_WithACallerSuppliedSample_LeavesItExactlyAsGiven()
    {
        // The counterpart rule: a caller who hands over a whole sample has already said what it
        // carries, so the channel does not overwrite its scaling.
        var channel = new AnalogChannel(0) { Scaling = new ChannelScaling(20.0, unit: "PSI") };

        channel.SetActiveSample(new DataSample(DateTime.UtcNow, 2.5));

        Assert.Null(channel.ActiveSample!.Scaling);
        Assert.Equal(2.5, channel.ActiveSample.ScaledValue);
    }

    [Fact]
    public void ReconfiguringScaling_DoesNotReinterpretSamplesAlreadyTaken()
    {
        // The reason the scaling travels on the sample rather than being read back off the channel:
        // a reading taken in volts must not silently become a pressure an hour later.
        var channel = new AnalogChannel(0) { Scaling = new ChannelScaling(1.0, unit: "V") };
        channel.SetActiveSample(2.5, DateTime.UtcNow);
        var before = channel.ActiveSample!;

        channel.Scaling = new ChannelScaling(20.0, unit: "PSI");

        Assert.Equal(2.5, before.ScaledValue);
        Assert.Equal("V", before.Unit);
    }

    [Fact]
    public void Scaling_CanBeClearedBackToNull()
    {
        var channel = new AnalogChannel(0) { Scaling = new ChannelScaling(20.0, unit: "PSI") };

        channel.Scaling = null;
        channel.SetActiveSample(2.5, DateTime.UtcNow);

        Assert.Null(channel.ActiveSample!.Unit);
        Assert.Equal(2.5, channel.ActiveSample.ScaledValue);
    }

    [Fact]
    public void Scaling_SitsAboveTheDeviceCalibration_RatherThanReplacingIt()
    {
        // Two conversions, in order: counts -> volts by the device's calibration, volts ->
        // engineering units by the caller's scaling. Getting this backwards (or collapsing the two)
        // is the failure this pins.
        var channel = new AnalogChannel(0, resolution: 65535)
        {
            PortRange = 10.0,
            CalibrationM = 1.0,
            InternalScaleM = 1.0,
            CalibrationB = 0.0,
            Scaling = new ChannelScaling(gain: 20.0, unit: "PSI")
        };

        var volts = channel.GetScaledValue(32768); // ~5 V
        channel.SetActiveSample(volts, DateTime.UtcNow);

        Assert.Equal(5.0, channel.ActiveSample!.Value, precision: 2);
        Assert.Equal(100.0, channel.ActiveSample.ScaledValue, precision: 1);
    }

    #endregion
}
