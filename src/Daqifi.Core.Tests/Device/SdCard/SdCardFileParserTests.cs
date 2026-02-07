using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.SdCard;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public class SdCardFileParserTests
{
    private readonly SdCardFileParser _parser = new();

    #region Status + Stream messages

    [Fact]
    public async Task ParseAsync_WithStatusAndStreamMessages_ExtractsConfigAndSamples()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(
                analogPortNum: 4,
                digitalPortNum: 2,
                timestampFreq: 50_000_000,
                firmwareRevision: "2.1.0",
                partNumber: "Nyquist1",
                serialNumber: 99999))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogFloatValues: new[] { 1.1f, 2.2f, 3.3f, 4.4f },
                digitalData: new byte[] { 0x0F }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 51_000_000, // 1 second later at 50 MHz
                analogFloatValues: new[] { 5.5f, 6.6f, 7.7f, 8.8f },
                digitalData: new byte[] { 0xF0 }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin");

        // Assert — config
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(4, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(2, session.DeviceConfig.DigitalPortCount);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);
        Assert.Equal("2.1.0", session.DeviceConfig.FirmwareRevision);
        Assert.Equal("Nyquist1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("99999", session.DeviceConfig.DeviceSerialNumber);

        // Assert — samples
        var samples = await ToListAsync(session.Samples);
        Assert.Equal(2, samples.Count);

        Assert.Equal(4, samples[0].AnalogValues.Count);
        Assert.InRange(samples[0].AnalogValues[0], 1.09, 1.11);
        Assert.Equal(0x0Fu, samples[0].DigitalData);

        Assert.Equal(4, samples[1].AnalogValues.Count);
        Assert.InRange(samples[1].AnalogValues[0], 5.49, 5.51);
        Assert.Equal(0xF0u, samples[1].DigitalData);

        // Second sample should be ~1 second after the first
        var timeDiff = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(timeDiff, 0.9, 1.1);
    }

    #endregion

    #region Stream data only (no status message)

    [Fact]
    public async Task ParseAsync_WithStreamDataOnly_ReturnsNullDeviceConfig()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 5000,
                analogFloatValues: new[] { 1.0f, 2.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 10000,
                analogFloatValues: new[] { 3.0f, 4.0f }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "data.bin");

        // Assert
        Assert.Null(session.DeviceConfig);

        var samples = await ToListAsync(session.Samples);
        Assert.Equal(2, samples.Count);
        Assert.Equal(2, samples[0].AnalogValues.Count);
        Assert.InRange(samples[0].AnalogValues[0], 0.99, 1.01);
    }

    #endregion

    #region Config scanning from stream messages

    [Fact]
    public async Task ParseAsync_WithConfigFieldsInStreamMessages_ExtractsConfig()
    {
        // Arrange — no dedicated status message, but streaming messages include TimestampFreq
        var msg1 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 100_000_000,
            analogFloatValues: new[] { 1.0f, 2.0f });
        msg1.TimestampFreq = 50_000_000; // 50 MHz clock embedded in stream message

        var msg2 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 150_000_000, // 1 second later at 50 MHz
            analogFloatValues: new[] { 3.0f, 4.0f });

        var builder = new SdCardTestFileBuilder()
            .AddMessage(msg1)
            .AddMessage(msg2);

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240115_103000.bin");

        // Assert — config should be extracted from scanning
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);

        // Timestamps should be properly reconstructed (not all the same)
        var samples = await ToListAsync(session.Samples);
        Assert.Equal(2, samples.Count);
        var timeDiff = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(timeDiff, 0.9, 1.1); // ~1 second apart
    }

    [Fact]
    public async Task ParseAsync_WithFirstStreamMessageContainingConfig_DoesNotSkipFirstSample()
    {
        // Arrange — first stream message carries config fields and sample data
        var msg1 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 100_000_000,
            analogFloatValues: new[] { 1.0f, 2.0f });
        msg1.TimestampFreq = 50_000_000;
        msg1.AnalogInPortNum = 2; // Embedded config field in stream message

        var msg2 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 150_000_000, // 1 second later at 50 MHz
            analogFloatValues: new[] { 3.0f, 4.0f });

        var builder = new SdCardTestFileBuilder()
            .AddMessage(msg1)
            .AddMessage(msg2);

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "embedded_config_first_message.bin");

        // Assert — first message should still be treated as a stream sample
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(2, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);

        var samples = await ToListAsync(session.Samples);
        Assert.Equal(2, samples.Count);
        Assert.InRange(samples[0].AnalogValues[0], 0.99, 1.01);
        Assert.InRange(samples[1].AnalogValues[0], 2.99, 3.01);

        var timeDiff = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(timeDiff, 0.9, 1.1);
    }

    [Fact]
    public async Task ParseAsync_WithConfigScatteredAcrossMessages_MergesConfig()
    {
        // Arrange — config fields spread across different messages
        var msg1 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 1000,
            analogFloatValues: new[] { 1.0f });
        msg1.TimestampFreq = 80_000_000;
        msg1.DeviceSn = 123456789;

        var msg2 = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 2000,
            analogFloatValues: new[] { 2.0f });
        msg2.DevicePn = "Nyquist1";
        msg2.DeviceFwRev = "3.2.0";

        var builder = new SdCardTestFileBuilder()
            .AddMessage(msg1)
            .AddMessage(msg2);

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "scattered.bin");

        // Assert — config merges fields from all messages
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(80_000_000u, session.DeviceConfig.TimestampFrequency);
        Assert.Equal("123456789", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal("Nyquist1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("3.2.0", session.DeviceConfig.FirmwareRevision);
    }

    [Fact]
    public async Task ParseAsync_WithStatusMessageMissingTimestampFreq_FillsFromScan()
    {
        // Arrange — status message has port counts but no TimestampFreq,
        // a later stream message has TimestampFreq
        var statusMsg = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 2
        };

        var streamMsg = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 100_000_000,
            analogFloatValues: new[] { 1.0f, 2.0f, 3.0f, 4.0f });
        streamMsg.TimestampFreq = 50_000_000;

        var builder = new SdCardTestFileBuilder()
            .AddMessage(statusMsg)
            .AddMessage(streamMsg);

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "partial_status.bin");

        // Assert — merged config has both port counts and timestamp freq
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(4, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(2, session.DeviceConfig.DigitalPortCount);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_WithStatusMessageEmptyStrings_FillsFromScannedConfig()
    {
        // Arrange — status message has empty string fields, later stream message has values
        var statusMsg = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 2,
            DevicePn = string.Empty,
            DeviceFwRev = string.Empty
        };

        var streamMsg = SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 100_000_000,
            analogFloatValues: new[] { 1.0f, 2.0f, 3.0f, 4.0f });
        streamMsg.TimestampFreq = 50_000_000;
        streamMsg.DevicePn = "Nyquist1";
        streamMsg.DeviceFwRev = "3.2.0";

        var builder = new SdCardTestFileBuilder()
            .AddMessage(statusMsg)
            .AddMessage(streamMsg);

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "empty_status_strings.bin");

        // Assert — merge should treat empty strings as missing values
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("Nyquist1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("3.2.0", session.DeviceConfig.FirmwareRevision);
    }

    [Fact]
    public async Task ParseAsync_WithFallbackTimestampFrequency_ReconstructsTimestamps()
    {
        // Arrange — no config at all, but messages have valid timestamps
        // Simulate device firmware that doesn't include TimestampFreq
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 100_000_000,
                analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 105_000_000, // 5M ticks later = 0.1s at 50 MHz
                analogFloatValues: new[] { 2.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 110_000_000, // 10M ticks later = 0.2s at 50 MHz
                analogFloatValues: new[] { 3.0f }));

        using var stream = builder.Build();

        var options = new SdCardParseOptions
        {
            FallbackTimestampFrequency = 50_000_000, // 50 MHz fallback
            SessionStartTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var session = await _parser.ParseAsync(stream, "no_config.bin", options);

        // Assert — no config in file but timestamps should work via fallback
        var samples = await ToListAsync(session.Samples);
        Assert.Equal(3, samples.Count);

        // First sample at base time
        Assert.Equal(options.SessionStartTime.Value, samples[0].Timestamp);

        // Second sample ~0.1s later
        var diff1 = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(diff1, 0.09, 0.11);

        // Third sample ~0.2s from first
        var diff2 = (samples[2].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(diff2, 0.19, 0.21);
    }

    [Fact]
    public async Task ParseAsync_WithFallbackTimestampFrequency_FileFrequencyTakesPrecedence()
    {
        // Arrange — file has TimestampFreq, so fallback should be ignored
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 50_000_000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 100_000_000,
                analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 150_000_000, // 1 second at 50 MHz
                analogFloatValues: new[] { 2.0f }));

        using var stream = builder.Build();

        var options = new SdCardParseOptions
        {
            FallbackTimestampFrequency = 1_000_000, // Different frequency — should be ignored
            SessionStartTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var session = await _parser.ParseAsync(stream, "has_config.bin", options);

        // Assert — should use file's 50 MHz, not fallback's 1 MHz
        var samples = await ToListAsync(session.Samples);
        Assert.Equal(2, samples.Count);
        var diff = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(diff, 0.9, 1.1); // 1 second at 50 MHz, not 50 seconds at 1 MHz
    }

    #endregion

    #region Empty file

    [Fact]
    public async Task ParseAsync_WithEmptyFile_ReturnsEmptySession()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());

        // Act
        var session = await _parser.ParseAsync(stream, "empty.bin");

        // Assert
        Assert.Null(session.DeviceConfig);
        Assert.Equal("empty.bin", session.FileName);

        var samples = await ToListAsync(session.Samples);
        Assert.Empty(samples);
    }

    #endregion

    #region Truncated file

    [Fact]
    public async Task ParseAsync_WithTruncatedFile_ReturnsPartialResults()
    {
        // Arrange — write one good message, then truncated data
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogFloatValues: new[] { 1.0f, 2.0f }))
            .AddRawBytes(new byte[] { 0x08, 0xFF, 0xFF }); // incomplete varint + partial payload

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "truncated.bin");

        // Assert — should get at least the first valid message
        var samples = await ToListAsync(session.Samples);
        Assert.True(samples.Count >= 1);
        Assert.InRange(samples[0].AnalogValues[0], 0.99, 1.01);
    }

    #endregion

    #region End-of-file marker

    [Fact]
    public async Task ParseAsync_WithEndOfFileMarker_StripsMarkerAndParsesSamples()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 5000,
                analogFloatValues: new[] { 3.14f }))
            .AddRawBytes(Encoding.ASCII.GetBytes("__END_OF_FILE__"));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "usb_transfer.bin");

        // Assert
        var samples = await ToListAsync(session.Samples);
        Assert.Single(samples);
        Assert.InRange(samples[0].AnalogValues[0], 3.13, 3.15);
    }

    #endregion

    #region Timestamp reconstruction

    [Fact]
    public async Task ParseAsync_WithTimestampFrequency_ReconstructsAbsoluteTimestamps()
    {
        // Arrange — 50 MHz clock, so 50_000_000 ticks = 1 second
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 50_000_000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 100_000_000,
                analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 150_000_000, // 1 second later
                analogFloatValues: new[] { 2.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 200_000_000, // 2 seconds from first
                analogFloatValues: new[] { 3.0f }));

        using var stream = builder.Build();

        var options = new SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240615_120000.bin", options);

        // Assert
        var samples = await ToListAsync(session.Samples);
        Assert.Equal(3, samples.Count);

        // First sample anchored at session start time
        Assert.Equal(options.SessionStartTime.Value, samples[0].Timestamp);

        // Second sample 1 second later
        var diff1 = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(diff1, 0.99, 1.01);

        // Third sample 2 seconds after first
        var diff2 = (samples[2].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.InRange(diff2, 1.99, 2.01);
    }

    #endregion

    #region Progress reporting

    [Fact]
    public async Task ParseAsync_ReportsProgressMonotonically()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder();
        for (var i = 0; i < 10; i++)
        {
            builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: (uint)(i * 1000),
                analogFloatValues: new[] { (float)i }));
        }

        using var stream = builder.Build();

        var progressReports = new List<SdCardParseProgress>();
        var options = new SdCardParseOptions
        {
            Progress = new Progress<SdCardParseProgress>(p => progressReports.Add(p))
        };

        // Act
        var session = await _parser.ParseAsync(stream, "progress.bin", options);
        // Enumerate samples to force full parse
        await ToListAsync(session.Samples);

        // Allow progress handler to execute (Progress<T> posts to SynchronizationContext)
        await Task.Delay(100);

        // Assert — at least one progress report
        Assert.NotEmpty(progressReports);

        // BytesRead should be non-decreasing
        for (var i = 1; i < progressReports.Count; i++)
        {
            Assert.True(progressReports[i].BytesRead >= progressReports[i - 1].BytesRead,
                $"BytesRead decreased at index {i}: {progressReports[i].BytesRead} < {progressReports[i - 1].BytesRead}");
        }
    }

    #endregion

    #region ParseFileAsync convenience method

    [Fact]
    public async Task ParseFileAsync_ParsesLocalFile()
    {
        // Arrange — write a temp file
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1_000_000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 500,
                analogFloatValues: new[] { 42.0f }));

        var tempPath = Path.Combine(Path.GetTempPath(), "log_20240115_103000.bin");
        try
        {
            await File.WriteAllBytesAsync(tempPath, builder.Build().ToArray());

            // Act
            var session = await _parser.ParseFileAsync(tempPath);

            // Assert
            Assert.Equal("log_20240115_103000.bin", session.FileName);
            Assert.NotNull(session.FileCreatedDate);
            Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), session.FileCreatedDate);
            Assert.NotNull(session.DeviceConfig);

            var samples = await ToListAsync(session.Samples);
            Assert.Single(samples);
            Assert.InRange(samples[0].AnalogValues[0], 41.99, 42.01);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    #endregion

    #region Analog int data fallback

    [Fact]
    public async Task ParseAsync_WithIntAnalogData_FallsBackToIntValues()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogIntValues: new[] { 100, 200, 300 }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "int_data.bin");

        // Assert
        var samples = await ToListAsync(session.Samples);
        Assert.Single(samples);
        Assert.Equal(3, samples[0].AnalogValues.Count);
        Assert.Equal(100.0, samples[0].AnalogValues[0]);
        Assert.Equal(200.0, samples[0].AnalogValues[1]);
        Assert.Equal(300.0, samples[0].AnalogValues[2]);
    }

    #endregion

    #region Per-channel timestamps

    [Fact]
    public async Task ParseAsync_WithAnalogTimestamps_PreservesPerChannelTimestamps()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogFloatValues: new[] { 1.0f, 2.0f },
                analogTimestamps: new uint[] { 900, 1100 }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "ts.bin");

        // Assert
        var samples = await ToListAsync(session.Samples);
        Assert.Single(samples);
        Assert.NotNull(samples[0].AnalogTimestamps);
        Assert.Equal(2, samples[0].AnalogTimestamps!.Count);
        Assert.Equal(900u, samples[0].AnalogTimestamps![0]);
        Assert.Equal(1100u, samples[0].AnalogTimestamps![1]);
    }

    #endregion

    #region Filename date parsing

    [Fact]
    public async Task ParseAsync_WithLogFilename_ExtractsDateFromFilename()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240320_153045.bin");

        // Assert
        Assert.NotNull(session.FileCreatedDate);
        Assert.Equal(new DateTime(2024, 3, 20, 15, 30, 45), session.FileCreatedDate);
    }

    [Fact]
    public async Task ParseAsync_WithSessionStartTimeOverride_UsesOverride()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());
        var overrideTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var options = new SdCardParseOptions { SessionStartTime = overrideTime };

        // Act
        var session = await _parser.ParseAsync(stream, "log_20240320_153045.bin", options);

        // Assert — override takes precedence over filename
        Assert.Equal(overrideTime, session.FileCreatedDate);
    }

    #endregion

    #region Calibration extraction

    [Fact]
    public async Task ParseAsync_WithCalibrationData_ExtractsCalibrationValues()
    {
        // Arrange
        var statusMsg = SdCardTestFileBuilder.CreateStatusMessage();
        statusMsg.AnalogInCalM.AddRange(new[] { 1.0f, 1.1f, 1.2f });
        statusMsg.AnalogInCalB.AddRange(new[] { 0.0f, 0.01f, 0.02f });

        var builder = new SdCardTestFileBuilder()
            .AddMessage(statusMsg)
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogFloatValues: new[] { 1.0f }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "cal.bin");

        // Assert
        Assert.NotNull(session.DeviceConfig);
        Assert.NotNull(session.DeviceConfig.CalibrationValues);
        Assert.Equal(3, session.DeviceConfig.CalibrationValues!.Count);
        Assert.Equal(1.0, session.DeviceConfig.CalibrationValues![0].Slope, 2);
        Assert.Equal(0.01, session.DeviceConfig.CalibrationValues![1].Intercept, 4);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task ParseAsync_WithCancelledToken_ThrowsOperationCancelled()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                analogFloatValues: new[] { 1.0f }));

        using var stream = builder.Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _parser.ParseAsync(stream, "cancelled.bin", ct: cts.Token));
    }

    #endregion

    #region Input validation

    [Fact]
    public async Task ParseAsync_WithNonPositiveBufferSize_ThrowsArgumentOutOfRange()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());
        var options = new SdCardParseOptions { BufferSize = 0 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _parser.ParseAsync(stream, "test.bin", options));
    }

    [Fact]
    public async Task ParseAsync_WithNegativeBufferSize_ThrowsArgumentOutOfRange()
    {
        // Arrange
        using var stream = new MemoryStream(Array.Empty<byte>());
        var options = new SdCardParseOptions { BufferSize = -1 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _parser.ParseAsync(stream, "test.bin", options));
    }

    #endregion

    #region Digital-only data

    [Fact]
    public async Task ParseAsync_WithDigitalOnlyData_ParsesCorrectly()
    {
        // Arrange
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: 1000,
                digitalData: new byte[] { 0xAB, 0xCD }));

        using var stream = builder.Build();

        // Act
        var session = await _parser.ParseAsync(stream, "digital.bin");

        // Assert
        var samples = await ToListAsync(session.Samples);
        Assert.Single(samples);
        Assert.Empty(samples[0].AnalogValues);
        Assert.Equal(0xCDABu, samples[0].DigitalData); // little-endian
    }

    #endregion

    #region Helpers

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    #endregion
}
