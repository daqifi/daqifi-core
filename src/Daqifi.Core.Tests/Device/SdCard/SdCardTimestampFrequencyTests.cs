using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Tests for how an SD card log's timestamp clock frequency is chosen and reported (issue #426).
/// </summary>
/// <remarks>
/// Firmware v3.7.2 and earlier write no <c>TimestampFreq</c> into SD card logs but do report one
/// (42 MHz on the bench Nq1) in their live status message. Before the fix, the live figure was
/// discarded and parsing fell back to 50 MHz, silently stretching every reconstructed timestamp
/// by a factor of 50/42 ≈ 1.19.
/// </remarks>
public class SdCardTimestampFrequencyTests
{
    private const uint DeviceFrequencyHz = 42_000_000;

    // 20 Hz at a 42 MHz counter: 42e6 / 20 = 2,100,000 ticks per sample. Measured on the bench.
    private const uint TicksPerSampleAt20Hz = 2_100_000;

    private readonly SdCardFileParser _parser = new();

    #region SdCardDeviceConfiguration.FromDevice

    [Fact]
    public void FromDevice_WithDeviceReportedFrequency_PropagatesIt()
    {
        // Arrange — a device whose status message reported a real timestamp clock.
        var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 2,
            TimestampFreq = DeviceFrequencyHz
        });

        // Act
        var config = SdCardDeviceConfiguration.FromDevice(device);

        // Assert — the one field the live device is uniquely able to supply is carried across.
        Assert.NotNull(config);
        Assert.Equal(DeviceFrequencyHz, config.TimestampFrequency);
    }

    [Fact]
    public void FromDevice_WhenDeviceReportedNoFrequency_KeepsZero()
    {
        // Arrange — status message with channel counts but no TimestampFreq.
        var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 2
        });

        // Act
        var config = SdCardDeviceConfiguration.FromDevice(device);

        // Assert — zero means "unknown", which leaves the parser's fallback in charge.
        Assert.NotNull(config);
        Assert.Equal(0u, config.TimestampFrequency);
    }

    [Fact]
    public void FromDevice_WithNoAnalogChannels_ReturnsNull()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            DigitalPortNum = 2,
            TimestampFreq = DeviceFrequencyHz
        });

        // Act & Assert
        Assert.Null(SdCardDeviceConfiguration.FromDevice(device));
    }

    #endregion

    #region Resolver precedence

    [Theory]
    // File wins outright, even against a device and a fallback.
    [InlineData(80_000_000u, 42_000_000u, 50_000_000u, 80_000_000u, SdCardTimestampSource.LogFile)]
    // File silent: the device's real clock beats the fallback guess.
    [InlineData(0u, 42_000_000u, 50_000_000u, 42_000_000u, SdCardTimestampSource.Device)]
    // Nothing but the guess.
    [InlineData(0u, 0u, 50_000_000u, 50_000_000u, SdCardTimestampSource.Fallback)]
    // Guess disabled: no conversion at all rather than a wrong one.
    [InlineData(0u, 0u, 0u, 0u, SdCardTimestampSource.None)]
    // A device that reports nothing does not shadow the fallback.
    [InlineData(0u, 0u, 1_000u, 1_000u, SdCardTimestampSource.Fallback)]
    public void Resolve_FollowsFileThenDeviceThenFallback(
        uint fileHz,
        uint deviceHz,
        uint fallbackHz,
        uint expectedHz,
        SdCardTimestampSource expectedSource)
    {
        var (frequencyHz, source) = SdCardTimestampFrequencyResolver.Resolve(fileHz, deviceHz, fallbackHz);

        Assert.Equal(expectedHz, frequencyHz);
        Assert.Equal(expectedSource, source);
    }

    #endregion

    #region Parser reports which frequency it used

    [Fact]
    public async Task ParseAsync_WithFileEmbeddedFrequency_PrefersFileOverDevice()
    {
        // Arrange — the file states 80 MHz while a connected device claims 42 MHz.
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(
                analogPortNum: 2,
                digitalPortNum: 1,
                timestampFreq: 80_000_000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1_000,
                analogFloatValues: new[] { 1.0f, 2.0f }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            ConfigurationOverride = DeviceOverride(DeviceFrequencyHz)
        });

        // Assert — a self-describing log is never overridden by a live device.
        Assert.Equal(80_000_000u, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.LogFile, session.TimestampFrequencySource);
    }

    [Fact]
    public async Task ParseAsync_WhenFileHasNoFrequency_UsesDeviceAndReportsIt()
    {
        // Arrange — a FW 3.7.2-shaped log: stream messages only, no TimestampFreq anywhere.
        using var stream = BuildTwoSampleLogWithoutFrequency();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            ConfigurationOverride = DeviceOverride(DeviceFrequencyHz)
        });

        // Assert
        Assert.Equal(DeviceFrequencyHz, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.Device, session.TimestampFrequencySource);
    }

    [Fact]
    public async Task ParseAsync_WhenNothingSuppliesFrequency_SurfacesTheFallbackRatherThanHidingIt()
    {
        // Arrange — no file frequency, no connected device.
        using var stream = BuildTwoSampleLogWithoutFrequency();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            FallbackTimestampFrequency = 50_000_000
        });

        // Assert — the guess still happens, but the caller can now see that it did.
        Assert.Equal(50_000_000u, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.Fallback, session.TimestampFrequencySource);
    }

    [Fact]
    public async Task ParseAsync_WithFallbackDisabled_ReportsNoFrequency()
    {
        // Arrange
        using var stream = BuildTwoSampleLogWithoutFrequency();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            FallbackTimestampFrequency = 0
        });

        // Assert
        Assert.Equal(0u, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.None, session.TimestampFrequencySource);
    }

    #endregion

    #region Regression: the ~19% scaling error itself

    [Fact]
    public async Task ParseAsync_WithConnectedDevice_SpacesSamplesAtTheRecordedRate()
    {
        // Arrange — two samples one 20 Hz period apart on a 42 MHz counter, exactly as the
        // bench Nq1 writes them.
        using var stream = BuildTwoSampleLogWithoutFrequency();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            ConfigurationOverride = DeviceOverride(DeviceFrequencyHz)
        });

        var samples = await ToListAsync(session.Samples);

        // Assert — 50.0 ms apart, the rate the data was actually logged at. Falling back to
        // 50 MHz would report 42.0 ms, an 8 ms (19%) error on every interval in the file.
        Assert.Equal(2, samples.Count);
        var spacing = (samples[1].Timestamp - samples[0].Timestamp).TotalMilliseconds;
        Assert.Equal(50.0, spacing, precision: 3);
    }

    [Fact]
    public async Task ParseAsync_WithoutConnectedDevice_StillMisreportsButSaysSo()
    {
        // Arrange — the same file parsed offline, where the 50 MHz guess is all there is.
        using var stream = BuildTwoSampleLogWithoutFrequency();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin", new SdCardParseOptions
        {
            FallbackTimestampFrequency = 50_000_000
        });

        var samples = await ToListAsync(session.Samples);

        // Assert — this documents the residual limitation: with no device to ask, the spacing
        // is still wrong. What changed is that TimestampFrequencySource now says the figure was
        // a guess, so a caller can warn instead of silently trusting it.
        var spacing = (samples[1].Timestamp - samples[0].Timestamp).TotalMilliseconds;
        Assert.Equal(42.0, spacing, precision: 3);
        Assert.Equal(SdCardTimestampSource.Fallback, session.TimestampFrequencySource);
    }

    #endregion

    private static SdCardDeviceConfiguration DeviceOverride(uint timestampFrequencyHz) =>
        new(
            AnalogPortCount: 2,
            DigitalPortCount: 1,
            TimestampFrequency: timestampFrequencyHz,
            DeviceSerialNumber: "TEST123",
            DevicePartNumber: "Nq1",
            FirmwareRevision: "3.7.2",
            CalibrationValues: null);

    private static System.IO.Stream BuildTwoSampleLogWithoutFrequency()
    {
        return new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1_000_000,
                analogFloatValues: new[] { 1.0f, 2.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1_000_000 + TicksPerSampleAt20Hz,
                analogFloatValues: new[] { 3.0f, 4.0f }))
            .Build();
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
