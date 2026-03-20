using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public sealed class SdCardCsvFileParserTests
{
    // -------------------------------------------------------------------------
    // Basic parsing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_ValidFirmwareCsvLines_ReturnsCorrectSamples()
    {
        // Arrange — real firmware format: shared timestamp per row, 2 channels
        // Tick freq = 100 Hz → delta of 100 ticks = 1 second
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "AABBCCDDEEFF0011", 100u,
            (1000u, new[] { 1.5, 2.5 }),
            (1100u, new[] { 3.5, 4.5 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var session = await parser.ParseAsync(stream, "log_20240101_120000.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal("log_20240101_120000.csv", session.FileName);
        Assert.Equal(2, samples.Count);

        // First sample — at anchor time
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), samples[0].Timestamp);
        Assert.Equal(2, samples[0].AnalogValues.Count);
        Assert.Equal(1.5, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(2.5, samples[0].AnalogValues[1], precision: 5);

        // Second sample — 1 second later (100 ticks / 100 Hz = 1 s)
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 1, DateTimeKind.Utc), samples[1].Timestamp);
        Assert.Equal(3.5, samples[1].AnalogValues[0], precision: 5);
        Assert.Equal(4.5, samples[1].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_NoDioColumn_DigitalDataIsZero()
    {
        // Arrange — CSV without a dio column pair → digital data defaults to 0
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "SN123", 50_000_000u,
            (1000u, new[] { 10.0, 20.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.Equal(0u, samples[0].DigitalData);
    }

    // -------------------------------------------------------------------------
    // Header parsing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_ExtractsDeviceConfigFromCommentHeaders()
    {
        // Arrange — real firmware embeds all config in # comment lines
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "7E2815916200E898", 50_000_000u,
            (1746522255u, new[] { 0.0, 21.0, 0.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");

        // Assert
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("Nyquist 1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("7E2815916200E898", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);
        Assert.Equal(3, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(0, session.DeviceConfig.DigitalPortCount);
    }

    [Fact]
    public async Task ParseAsync_ChannelCountInferredFromColumnHeader()
    {
        // Arrange — 3 channels → 6 column headers (ch0_ts,ch0_val,ch1_ts,ch1_val,ch2_ts,ch2_val)
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 1000u,
            (500u, new[] { 1.0, 2.0, 3.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");

        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(3, session.DeviceConfig.AnalogPortCount);
    }

    [Fact]
    public async Task ParseAsync_NoCommentHeaders_UsesDefaultsAndParsesData()
    {
        // Arrange — column header + data rows only (no # comments)
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileNoHeaders(
            (1000u, new[] { 5.0, 10.0 }),
            (2000u, new[] { 6.0, 11.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 1000u
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(5.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(10.0, samples[0].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_FallbackTimestampFrequencyUsedWhenNoHeader()
    {
        // Arrange — no # Timestamp Tick Rate comment, falls back to options.FallbackTimestampFrequency
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileNoHeaders(
            (0u, new[] { 1.0 }),
            (500u, new[] { 2.0 })  // 500 ticks @ 500 Hz = 1 second
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FallbackTimestampFrequency = 500u
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), samples[0].Timestamp);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc), samples[1].Timestamp);
    }

    // -------------------------------------------------------------------------
    // Per-channel timestamps
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_PerChannelTimestamps_PopulatedFromFirmwareFormat()
    {
        // Arrange — real firmware CSV stores per-channel timestamps; each channel can have a different ts
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFile(
            channelCount: 2,
            deviceName: "TestDevice",
            serialNumber: "SN001",
            timestampFreq: 1000u,
            new[] { (1000u, 5.0), (1001u, 10.0) }  // ch0 ts=1000, ch1 ts=1001
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.NotNull(samples[0].AnalogTimestamps);
        Assert.Equal(2, samples[0].AnalogTimestamps!.Count);
        Assert.Equal(1000u, samples[0].AnalogTimestamps[0]);
        Assert.Equal(1001u, samples[0].AnalogTimestamps[1]);
    }

    [Fact]
    public async Task ParseAsync_SharedTimestampPerRow_PerChannelTimestampsAllEqual()
    {
        // Arrange — in practice, firmware writes the same timestamp for every channel in a row
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "SN001", 50_000_000u,
            (1746522255u, new[] { 0.0, 21.0, 0.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.NotNull(samples[0].AnalogTimestamps);
        Assert.Equal(3, samples[0].AnalogTimestamps!.Count);
        // All channels share the same timestamp in shared-ts mode
        Assert.Equal(1746522255u, samples[0].AnalogTimestamps[0]);
        Assert.Equal(1746522255u, samples[0].AnalogTimestamps[1]);
        Assert.Equal(1746522255u, samples[0].AnalogTimestamps[2]);
    }

    // -------------------------------------------------------------------------
    // Timestamp arithmetic
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_TimestampRollover_HandledCorrectly()
    {
        // Arrange — tick counter wraps from near-max to a small value
        var nearMax = uint.MaxValue - 50;

        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (nearMax, new[] { 1.0 }),
            (100u,    new[] { 2.0 })   // rollover
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        // delta = (uint.MaxValue - nearMax) + 100 + 1 = 51 + 100 = 151 ticks @ 100 Hz = 1.51 s
        Assert.Equal(2, samples.Count);
        var expectedDelta = 151.0 / 100.0;
        Assert.Equal(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expectedDelta),
            samples[1].Timestamp);
    }

    [Fact]
    public async Task ParseAsync_MonotonicallyIncreasingTimestamps_AdvanceCorrectly()
    {
        // Arrange — simulate real device data (50 MHz tick, ~5 ms between rows = 250 000 ticks)
        const uint freq = 50_000_000u;
        const uint delta = 250_000u;  // 5 ms worth of ticks

        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "SN001", freq,
            (1_000_000u, new[] { 1.0 }),
            (1_000_000u + delta, new[] { 2.0 }),
            (1_000_000u + delta * 2, new[] { 3.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(3, samples.Count);
        Assert.True(samples[1].Timestamp > samples[0].Timestamp);
        Assert.True(samples[2].Timestamp > samples[1].Timestamp);

        var dt = (samples[1].Timestamp - samples[0].Timestamp).TotalSeconds;
        Assert.Equal(0.005, dt, precision: 6);  // 5 ms
    }

    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_EmptyFile_ReturnsEmptySession()
    {
        await using var stream = new MemoryStream();

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Empty(samples);
        Assert.Null(session.DeviceConfig);
    }

    [Fact]
    public async Task ParseAsync_HeaderOnlyNoDataRows_ReturnsEmptySamples()
    {
        // Arrange — only comment lines + column header, no data
        var content = "# Device: Nyquist 1\n# Serial Number: SN001\n# Timestamp Tick Rate: 50000000 Hz\nch0_ts,ch0_val\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Empty(samples);
        Assert.NotNull(session.DeviceConfig);  // Config extracted from headers
        Assert.Equal("Nyquist 1", session.DeviceConfig!.DevicePartNumber);
    }

    [Fact]
    public async Task ParseAsync_MalformedDataRow_SkipsAndContinues()
    {
        // Arrange — embed a malformed row between valid ones
        var content =
            "# Device: TestDevice\n" +
            "# Serial Number: SN001\n" +
            "# Timestamp Tick Rate: 100 Hz\n" +
            "ch0_ts,ch0_val\n" +
            "1000,5.0\n" +
            "not_a_number,bad\n" +   // should be skipped
            "2000,6.0\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(5.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(6.0, samples[1].AnalogValues[0], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_OddColumnCount_SkipsMalformedRow()
    {
        // Arrange — a row with an odd number of columns (not ts+val pairs) should be skipped
        var content =
            "# Device: TestDevice\n" +
            "# Serial Number: SN001\n" +
            "# Timestamp Tick Rate: 100 Hz\n" +
            "ch0_ts,ch0_val,ch1_ts,ch1_val\n" +
            "1000,5.0,1001,10.0\n" +  // valid: 2 channels
            "2000,6.0,2001\n" +        // odd column count — skip
            "3000,7.0,3001,12.0\n";   // valid
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(5.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(7.0, samples[1].AnalogValues[0], precision: 5);
    }

    // -------------------------------------------------------------------------
    // Date / config extraction
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_FileNameDateExtraction_SetsFileCreatedDate()
    {
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (1000u, new[] { 1.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "log_20240315_143022.csv");

        Assert.NotNull(session.FileCreatedDate);
        Assert.Equal(new DateTime(2024, 3, 15, 14, 30, 22), session.FileCreatedDate.Value);
    }

    [Fact]
    public async Task ParseAsync_SessionStartTimeOverride_TakesPrecedenceOverFileName()
    {
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (1000u, new[] { 1.0 })
        );

        var overrideTime = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = overrideTime
        };

        var session = await parser.ParseAsync(stream, "log_20240315_143022.csv", options);

        // SessionStartTime wins over filename-derived date
        Assert.Equal(overrideTime, session.FileCreatedDate);
    }

    [Fact]
    public async Task ParseAsync_ConfigurationOverride_OverridesEmbeddedHeaders()
    {
        // Arrange — file has headers; override fills gaps (FirmwareRevision, DigitalPortCount)
        // while file-parsed values take precedence for fields the file already provides.
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "7E2815916200E898", 50_000_000u,
            (1000u, new[] { 1.0, 2.0 })
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 4,
            DigitalPortCount: 1,
            TimestampFrequency: 1_000_000u,
            DeviceSerialNumber: "OVERRIDE_SN",
            DevicePartNumber: "OVERRIDE_DEVICE",
            FirmwareRevision: "9.9.9",
            CalibrationValues: null
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);

        Assert.NotNull(session.DeviceConfig);
        // File-parsed values take precedence
        Assert.Equal("7E2815916200E898", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal("Nyquist 1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal(50_000_000u, session.DeviceConfig.TimestampFrequency);
        // Override fills gaps not present in the file
        Assert.Equal("9.9.9", session.DeviceConfig.FirmwareRevision);
        Assert.Equal(1, session.DeviceConfig.DigitalPortCount);
    }

    // -------------------------------------------------------------------------
    // Progress reporting & cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_ProgressReporting_CallsCallback()
    {
        // Arrange — 250 rows to trigger intermediate progress reports (every 100 lines)
        var rows = Enumerable.Range(0, 250)
            .Select(i => ((uint)(i * 100), new[] { (double)i }))
            .ToArray();

        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u, rows);

        var progressCalls = 0;
        var lastProgress = default(global::Daqifi.Core.Device.SdCard.SdCardParseProgress);
        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            Progress = new Progress<global::Daqifi.Core.Device.SdCard.SdCardParseProgress>(p =>
            {
                progressCalls++;
                lastProgress = p;
            })
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(250, samples.Count);
        Assert.True(progressCalls >= 2, $"Expected ≥2 progress callbacks, got {progressCalls}");
        Assert.Equal(250, lastProgress.MessagesRead);
        Assert.True(lastProgress.BytesRead > 0);
    }

    [Fact]
    public async Task ParseAsync_CancellationDuringRead_ThrowsOperationCanceled()
    {
        // Pre-cancelled token → should throw during the ReadLineAsync phase
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            Enumerable.Range(0, 1000).Select(i => ((uint)i, new[] { (double)i })).ToArray());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await parser.ParseAsync(stream, "test.csv", ct: cts.Token);
        });
    }

    // -------------------------------------------------------------------------
    // ADC scaling
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_WithCalibrationConfig_ScalesRawAdcValues()
    {
        // Arrange — raw ADC values (e.g., 22 counts on a 16-bit ADC)
        // with calibration config that should scale them to voltage
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "7E2815916200E898", 50_000_000u,
            (1000u, new[] { 0.0, 22.0, 16.0 })
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 3,
            DigitalPortCount: 0,
            TimestampFrequency: 50_000_000u,
            DeviceSerialNumber: "7E2815916200E898",
            DevicePartNumber: "Nyquist 1",
            FirmwareRevision: "3.4.4",
            CalibrationValues: new[] { (1.0, 0.0), (1.0, 0.0), (1.0, 0.0) },
            Resolution: 65535,
            PortRange: new[] { 10.0, 10.0, 10.0 },
            InternalScaleM: new[] { 1.0, 1.0, 1.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert — raw value 22 should be scaled: (22 / 65535) * 10.0 * 1.0 + 0.0 = ~0.00336
        Assert.Single(samples);
        Assert.Equal(3, samples[0].AnalogValues.Count);
        Assert.Equal(0.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(22.0 / 65535.0 * 10.0, samples[0].AnalogValues[1], precision: 5);
        Assert.Equal(16.0 / 65535.0 * 10.0, samples[0].AnalogValues[2], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_WithoutCalibrationConfig_ReturnsRawValues()
    {
        // Arrange — no config override, no resolution → raw values pass through
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1", "SN001", 100u,
            (1000u, new[] { 22.0, 16.0 })
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        // No scaling applied — raw values returned as-is
        Assert.Single(samples);
        Assert.Equal(22.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(16.0, samples[0].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_WithCalibrationOffsets_AppliesFullFormula()
    {
        // Arrange — test the full scaling formula: (raw / resolution * portRange * calM + calB) * internalScaleM
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (1000u, new[] { 32768.0 })  // half-scale on 16-bit ADC
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 1,
            DigitalPortCount: 0,
            TimestampFrequency: 100u,
            DeviceSerialNumber: "SN001",
            DevicePartNumber: "TestDevice",
            FirmwareRevision: null,
            CalibrationValues: new[] { (1.02, -0.05) },  // calM=1.02, calB=-0.05
            Resolution: 65535,
            PortRange: new[] { 10.0 },
            InternalScaleM: new[] { 2.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Expected: (32768 / 65535 * 10.0 * 1.02 + (-0.05)) * 2.0
        var normalized = 32768.0 / 65535.0;
        var expected = (normalized * 10.0 * 1.02 + (-0.05)) * 2.0;
        Assert.Single(samples);
        Assert.Equal(expected, samples[0].AnalogValues[0], precision: 5);
    }

    // -------------------------------------------------------------------------
    // DIO column handling
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_WithDioColumn_ParsesDigitalDataSeparately()
    {
        // Arrange — firmware CSV with ain columns + dio column at the end
        var content =
            "# Device: Nyquist 1\n" +
            "# Serial Number: 7E2815916200E898\n" +
            "# Timestamp Tick Rate: 50000000 Hz\n" +
            "ain0_ts,ain0_val,ain1_ts,ain1_val,dio_ts,dio_val\n" +
            "1000,0,1000,22,1000,5\n" +
            "2000,1,2000,23,2000,3\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        // Assert — 2 analog channels (not 3), dio parsed as digital data
        Assert.Equal(2, session.DeviceConfig!.AnalogPortCount);
        Assert.Equal(1, session.DeviceConfig.DigitalPortCount);
        Assert.Equal(2, samples.Count);

        // Analog values (unscaled since no config override)
        Assert.Equal(2, samples[0].AnalogValues.Count);
        Assert.Equal(0.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(22.0, samples[0].AnalogValues[1], precision: 5);

        // Digital data
        Assert.Equal(5u, samples[0].DigitalData);
        Assert.Equal(3u, samples[1].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_WithDioColumnAndScaling_ScalesOnlyAnalogValues()
    {
        // Arrange — ain columns should be scaled, dio column should not
        var content =
            "# Device: Nyquist 1\n" +
            "# Serial Number: SN001\n" +
            "# Timestamp Tick Rate: 100 Hz\n" +
            "ain0_ts,ain0_val,dio_ts,dio_val\n" +
            "1000,32768,1000,7\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 1,
            DigitalPortCount: 1,
            TimestampFrequency: 100u,
            DeviceSerialNumber: "SN001",
            DevicePartNumber: "Nyquist 1",
            FirmwareRevision: null,
            CalibrationValues: new[] { (1.0, 0.0) },
            Resolution: 65535,
            PortRange: new[] { 10.0 },
            InternalScaleM: new[] { 1.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        // Analog should be scaled: 32768 / 65535 * 10.0 ≈ 5.0
        Assert.Equal(32768.0 / 65535.0 * 10.0, samples[0].AnalogValues[0], precision: 3);
        // Digital should remain as-is
        Assert.Equal(7u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_AinColumnHeader_CorrectlyParsed()
    {
        // Arrange — real firmware uses ain0, ain1, ain2 etc. as column prefixes
        var content =
            "# Device: Nyquist 1\n" +
            "# Serial Number: SN001\n" +
            "# Timestamp Tick Rate: 50000000 Hz\n" +
            "ain0_ts,ain0_val,ain1_ts,ain1_val,ain2_ts,ain2_val\n" +
            "1000,10,1000,20,1000,30\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "test.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(3, session.DeviceConfig!.AnalogPortCount);
        Assert.Equal(0, session.DeviceConfig.DigitalPortCount);
        Assert.Single(samples);
        Assert.Equal(3, samples[0].AnalogValues.Count);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

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
