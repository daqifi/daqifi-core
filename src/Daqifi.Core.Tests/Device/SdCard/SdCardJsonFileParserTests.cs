using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public sealed class SdCardJsonFileParserTests
{
    [Fact]
    public async Task ParseAsync_ValidJsonLines_ReturnsCorrectSamples()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.234567, 2.345678 }, "01-02"),
            (200u, new[] { 3.456789, 4.567890 }, "03-04")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            FallbackTimestampFrequency = 100  // 100 Hz for easy math
        };

        // Act
        var session = await parser.ParseAsync(stream, "log_20240101_120000.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal("log_20240101_120000.json", session.FileName);
        Assert.Equal(2, samples.Count);

        // First sample at anchor time
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), samples[0].Timestamp);
        Assert.Equal(2, samples[0].AnalogValues.Count);
        Assert.Equal(1.234567, samples[0].AnalogValues[0], precision: 6);
        Assert.Equal(2.345678, samples[0].AnalogValues[1], precision: 6);
        Assert.Equal(0x0201u, samples[0].DigitalData);  // Little-endian: 01-02

        // Second sample 1 second later (delta=100 ticks / 100 Hz = 1 second)
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 1, DateTimeKind.Utc), samples[1].Timestamp);
        Assert.Equal(3.456789, samples[1].AnalogValues[0], precision: 6);
        Assert.Equal(0x0403u, samples[1].DigitalData);  // Little-endian: 03-04
    }

    [Fact]
    public async Task ParseAsync_TimestampRollover_HandlesCorrectly()
    {
        // Arrange
        var nearMax = uint.MaxValue - 50;
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (nearMax, new[] { 1.0 }, ""),
            (100u, new[] { 2.0 }, "")  // Rollover
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(2, samples.Count);
        // Delta = (uint.MaxValue - nearMax) + 100 + 1 = 51 + 100 = 151 ticks
        var expectedDelta = 151.0 / 100.0;  // 1.51 seconds
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expectedDelta),
                     samples[1].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_IntegerAnalogValues_ParsesCorrectly()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFileWithIntegers(
            (100u, new[] { 1234, 5678 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(1234.0, samples[0].AnalogValues[0]);
        Assert.Equal(5678.0, samples[0].AnalogValues[1]);
    }

    [Fact]
    public async Task ParseAsync_EmptyDigitalField_ReturnsZero()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(0u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_DigitalHexString_ParsesLittleEndian()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, "01-02-03-04")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        // Little-endian: byte0=01, byte1=02, byte2=03, byte3=04
        // Result = 0x04030201
        Assert.Equal(0x04030201u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_MalformedLine_SkipsAndContinues()
    {
        // Arrange
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.StreamWriter(stream, leaveOpen: true);
        writer.WriteLine("{\"ts\":100,\"analog\":[1.0],\"digital\":\"\"}");
        writer.WriteLine("this is not valid json");
        writer.WriteLine("{\"ts\":200,\"analog\":[2.0],\"digital\":\"\"}");
        writer.Flush();
        stream.Position = 0;

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(2, samples.Count);  // Malformed line skipped
        Assert.Equal(1.0, samples[0].AnalogValues[0]);
        Assert.Equal(2.0, samples[1].AnalogValues[0]);
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_ReturnsEmptySamples()
    {
        // Arrange
        await using var stream = new System.IO.MemoryStream();

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions();

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Empty(samples);
        Assert.Null(session.DeviceConfig);
    }

    [Fact]
    public async Task ParseAsync_ConfigurationOverride_UsesProvidedConfig()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0, 2.0 }, "")
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 2,
            DigitalPortCount: 1,
            TimestampFrequency: 1000,
            DeviceSerialNumber: "TEST123",
            DevicePartNumber: "NQ1",
            FirmwareRevision: "1.0.0",
            CalibrationValues: null
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);

        // Assert
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("TEST123", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal(1000u, session.DeviceConfig.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_InfersAnalogChannelCount()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0, 2.0, 3.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 50_000_000
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);

        // Assert
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(3, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_ProgressReporting_CallsCallback()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            Enumerable.Range(0, 250).Select(i => ((uint)i, new[] { (double)i }, "")).ToArray()
        );

        var progressCalls = 0;
        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100,
            Progress = new Progress<global::Daqifi.Core.Device.SdCard.SdCardParseProgress>(p =>
            {
                progressCalls++;
                Assert.True(p.BytesRead >= 0);
                Assert.Equal(250, p.MessagesRead);
            })
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(250, samples.Count);
        Assert.True(progressCalls >= 2);  // At least 2 progress updates (100-line batches + final)
    }

    [Fact]
    public async Task ParseAsync_FileNameDateExtraction_SetsFileCreatedDate()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await parser.ParseAsync(stream, "log_20240315_143022.json", options);

        // Assert
        Assert.NotNull(session.FileCreatedDate);
        Assert.Equal(new DateTime(2024, 3, 15, 14, 30, 22), session.FileCreatedDate.Value);
    }

    [Fact]
    public async Task ParseAsync_CancellationToken_ThrowsOnCancel()
    {
        // Arrange — pre-cancelled token; the parser checks it during the ReadLineAsync loop
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            Enumerable.Range(0, 1000).Select(i => ((uint)i, new[] { (double)i }, "")).ToArray()
        );

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act & Assert — ParseAsync reads all lines upfront and throws when the token is already cancelled
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await parser.ParseAsync(stream, "test.json", options, cts.Token);
        });
    }

    [Fact]
    public async Task ParseAsync_PerChannelTimestamps_ReturnsNull()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Null(samples[0].AnalogTimestamps);  // JSON/CSV formats don't support per-channel timestamps
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
