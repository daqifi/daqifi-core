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
    public void Constructor_WithNoDecodeMetadata_DefaultsRawValueAndDeviceTimestamp()
    {
        // Samples not produced by the decode pipeline have no raw value or device timestamp.
        var sample = new DataSample(DateTime.UtcNow, 1.0);

        Assert.Null(sample.RawValue);
        Assert.Null(sample.DeviceTimestamp);
    }

    [Fact]
    public void Constructor_WithDecodeMetadata_SetsRawValueAndDeviceTimestamp()
    {
        var timestamp = new DateTime(2025, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        var sample = new DataSample(timestamp, 1.25, rawValue: 4321, deviceTimestamp: 987654u);

        Assert.Equal(timestamp, sample.Timestamp);
        Assert.Equal(1.25, sample.Value);
        Assert.Equal(4321, sample.RawValue);
        Assert.Equal(987654u, sample.DeviceTimestamp);
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

    #region Engineering units (#501)

    [Fact]
    public void WithNoScaling_ScaledValueIsTheValue_AndThereIsNoUnit()
    {
        // The compatibility guarantee: a consumer that never configures scaling sees exactly the
        // numbers it saw before the feature existed.
        var sample = new DataSample(DateTime.UtcNow, 2.5);

        Assert.Null(sample.Scaling);
        Assert.Equal(2.5, sample.ScaledValue);
        Assert.Null(sample.Unit);
    }

    [Fact]
    public void WithScaling_ScaledValueIsConverted_AndValueIsUntouched()
    {
        var sample = new DataSample(DateTime.UtcNow, 2.5, rawValue: 512, deviceTimestamp: 7)
        {
            Scaling = new ChannelScaling(gain: 20.0, offset: 1.0, unit: "PSI")
        };

        Assert.Equal(51.0, sample.ScaledValue);
        Assert.Equal(2.5, sample.Value); // nothing is converted in place
        Assert.Equal("PSI", sample.Unit);
        Assert.Equal(512, sample.RawValue);
    }

    [Fact]
    public void ScaledValue_TracksALaterValueWrite()
    {
        // Derived on read rather than stored at construction. Value still has a setter, and a
        // ScaledValue frozen at construction would silently disagree with it.
        var sample = new DataSample(DateTime.UtcNow, 1.0) { Scaling = new ChannelScaling(10.0) };

        sample.Value = 3.0;

        Assert.Equal(30.0, sample.ScaledValue);
    }

    [Fact]
    public void ScaledValue_OnAnOverflowingScaling_FallsBackToTheUnscaledValue()
    {
        var sample = new DataSample(DateTime.UtcNow, 1e10) { Scaling = new ChannelScaling(1e308) };

        Assert.Equal(1e10, sample.ScaledValue);
    }

    [Fact]
    public void AnImplementationPredatingScaling_StillSatisfiesTheInterface()
    {
        // What makes this an additive change rather than a breaking one: IDataSample's three new
        // members are defaulted, so an implementation written before they existed compiles and
        // reports "no scaling" — which is exactly what it has. This test would not compile if the
        // members were made abstract.
        IDataSample sample = new LegacySample(DateTime.UtcNow, 4.0);

        Assert.Null(sample.Scaling);
        Assert.Equal(4.0, sample.ScaledValue);
        Assert.Null(sample.Unit);
    }

    /// <summary>
    /// An <see cref="IDataSample"/> implementing only the members that existed before #501 — a
    /// stand-in for a consumer's own type out in the wild.
    /// </summary>
    private sealed class LegacySample(DateTime timestamp, double value) : IDataSample
    {
        public DateTime Timestamp { get; } = timestamp;

        public double Value { get; set; } = value;

        public int? RawValue => null;

        public uint? DeviceTimestamp => null;
    }

    #endregion
}
