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
    public async Task ParseAsync_LegacyLineWithoutDigitalField_ReportsZeroForThatSample()
    {
        // The legacy one-object-per-line shape is self-contained: a line that states no
        // "digital" field states a digital state of zero, and must not inherit the previous
        // line's. Pinned so the firmware "di" shape cannot quietly change this.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"ts\":100,\"analog\":[1.0],\"digital\":\"01-02\"}",
            "{\"ts\":200,\"analog\":[2.0]}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(0x0201u, samples[0].DigitalData);
        Assert.Equal(0u, samples[1].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_LegacyNonNumericAnalogValue_SkipsThatSample()
    {
        // A string inside the legacy "analog" array makes the whole sample unusable, so it is
        // dropped rather than reported with a guessed value.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"ts\":100,\"analog\":[1.0],\"digital\":\"\"}",
            "{\"ts\":200,\"analog\":[\"oops\"],\"digital\":\"\"}",
            "{\"ts\":300,\"analog\":[3.0],\"digital\":\"\"}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(1.0, samples[0].AnalogValues[0]);
        Assert.Equal(3.0, samples[1].AnalogValues[0]);
    }

    [Fact]
    public async Task ParseAsync_ObjectWithoutAnalogValues_IsNotASample()
    {
        // A timestamp on its own carries no reading, so it produces no sample.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"ts\":100,\"analog\":[1.0],\"digital\":\"\"}",
            "{\"ts\":200,\"digital\":\"\"}",
            "{\"ts\":300,\"analog\":[3.0],\"digital\":\"\"}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(1.0, samples[0].AnalogValues[0]);
        Assert.Equal(3.0, samples[1].AnalogValues[0]);
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
        // Arrange — JSON has no metadata headers, so override fills in device info gaps.
        // JSON lines carry no frequency of their own, so the connected device's frequency is
        // the best information available and must beat the FallbackTimestampFrequency guess.
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
        // Override fills gaps for metadata not present in JSON data
        Assert.Equal("TEST123", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal("NQ1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("1.0.0", session.DeviceConfig.FirmwareRevision);
        Assert.Equal(1, session.DeviceConfig.DigitalPortCount);
        // The device's reported frequency beats the fallback guess, and says so.
        Assert.Equal(1000u, session.DeviceConfig.TimestampFrequency);
        Assert.Equal(1000u, session.TimestampFrequency);
        Assert.Equal(
            global::Daqifi.Core.Device.SdCard.SdCardTimestampSource.Device,
            session.TimestampFrequencySource);
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
        global::Daqifi.Core.Device.SdCard.SdCardParseProgress? lastProgress = null;
        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100,
            Progress = new SynchronousProgress<global::Daqifi.Core.Device.SdCard.SdCardParseProgress>(p =>
            {
                progressCalls++;
                lastProgress = p;
            })
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(250, samples.Count);
        Assert.True(progressCalls >= 2);  // At least 2 progress updates (100-line batches + final)
        Assert.NotNull(lastProgress);
        Assert.Equal(250, lastProgress!.MessagesRead);
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

        // Act & Assert — ParseAsync reads all lines upfront and throws when the token is already cancelled.
        // ReadLineAsync surfaces cancellation as TaskCanceledException (a subclass of OperationCanceledException).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
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

    // -------------------------------------------------------------------------
    // ADC scaling
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_WithCalibrationConfig_ScalesRawAdcValues()
    {
        // Arrange — raw ADC integer values in JSON
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFileWithIntegers(
            (100u, new[] { 0, 22, 16 }, "00")
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 3,
            DigitalPortCount: 0,
            TimestampFrequency: 100u,
            DeviceSerialNumber: "SN001",
            DevicePartNumber: "TestDevice",
            FirmwareRevision: null,
            CalibrationValues: new[] { (1.0, 0.0), (1.0, 0.0), (1.0, 0.0) },
            Resolution: 65535,
            PortRange: new[] { 10.0, 10.0, 10.0 },
            InternalScaleM: new[] { 1.0, 1.0, 1.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FallbackTimestampFrequency = 100,
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert — raw 22 → (22 / 65535) * 10.0 ≈ 0.00336
        Assert.Single(samples);
        Assert.Equal(0.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(22.0 / 65535.0 * 10.0, samples[0].AnalogValues[1], precision: 5);
        Assert.Equal(16.0 / 65535.0 * 10.0, samples[0].AnalogValues[2], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_WithNonUnityIntScaleAndOffset_DoesNotScaleTheOffset()
    {
        // daqifi-core#387: calB is an offset in volts, added after the internal scale factor —
        // the SD path must agree with AnalogChannel.GetScaledValue and with the firmware.
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFileWithIntegers(
            (100u, new[] { 0, 32768, 0 }, "00")
        );

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 3,
            DigitalPortCount: 0,
            TimestampFrequency: 100u,
            DeviceSerialNumber: "SN001",
            DevicePartNumber: "TestDevice",
            FirmwareRevision: null,
            CalibrationValues: new[] { (1.0, -0.05), (1.0, -0.05), (1.0, -0.05) },
            Resolution: 65535,
            PortRange: new[] { 10.0, 10.0, 10.0 },
            InternalScaleM: new[] { 2.0, 2.0, 2.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FallbackTimestampFrequency = 100,
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);

        // At raw 0 the gain term vanishes, so the result is exactly calB — never calB * intScale.
        // Correct: -0.05.  Old (bug): -0.05 * 2.0 = -0.10.
        Assert.Equal(-0.05, samples[0].AnalogValues[0], precision: 6);

        // Correct:  32768 / 65535 * 10.0 * 1.0 * 2.0 + (-0.05)
        Assert.Equal(32768.0 / 65535.0 * 10.0 * 2.0 - 0.05, samples[0].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_WithoutCalibrationConfig_ReturnsRawValues()
    {
        // Arrange — no config override → values pass through as-is
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFileWithIntegers(
            (100u, new[] { 22, 16 }, "00")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.Equal(22.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(16.0, samples[0].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_WithTimestampFrequency_MarksHasDeviceTimestampTrue()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, ""),
            (200u, new[] { 2.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 100 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.True(s.HasDeviceTimestamp));
    }

    [Fact]
    public async Task ParseAsync_WithoutTimestampFrequency_MarksHasDeviceTimestampFalse()
    {
        // Arrange — no timestamp frequency available, so ticks can't be converted
        // and every entry falls back to the session base time.
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, ""),
            (200u, new[] { 2.0 }, "")
        );

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions { FallbackTimestampFrequency = 0 };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.False(s.HasDeviceTimestamp));
    }

    // -------------------------------------------------------------------------
    // The real firmware shape (daqifi-core#847)
    //
    // Every fixture below is literal log text in the shape JSON_Encoder.c writes: a one-line
    // {"meta":...} header, then sample objects that open with a bare "{" and spend one line on
    // each field, with one {"ch","val"} entry per enabled channel and several samples packed
    // into one object as repeated "ts"/"ai" keys.
    // -------------------------------------------------------------------------

    private const string FirmwareMetaHeader =
        "{\"meta\":{\"dev\":\"Nyquist 1\",\"sn\":\"7E2815916200E898\",\"tick_hz\":42000000}}";

    [Fact]
    public async Task ParseAsync_FirmwareObjectStream_ReturnsEverySample()
    {
        // Arrange — one object carrying two samples of four channels.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":2103674123,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":0.0012},",
            "{\"ch\":1, \"val\":0.0024},",
            "{\"ch\":2, \"val\":0.0000},",
            "{\"ch\":3, \"val\":0.0000}",
            "],",
            "\"ts\":2103674223,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":0.0013},",
            "{\"ch\":1, \"val\":0.0025},",
            "{\"ch\":2, \"val\":0.0000},",
            "{\"ch\":3, \"val\":0.0000}",
            "]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var session = await parser.ParseAsync(stream, "log_20261031_090000.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert — both samples, both four channels wide.
        Assert.Equal(2, samples.Count);
        Assert.Equal(new[] { 0.0012, 0.0024, 0.0, 0.0 }, samples[0].AnalogValues);
        Assert.Equal(new[] { 0.0013, 0.0025, 0.0, 0.0 }, samples[1].AnalogValues);

        // The two timestamps are 100 ticks apart on the 42 MHz clock the header states. One
        // DateTime tick is 100 ns, which is coarser than one device tick, so allow for that.
        Assert.Equal(options.SessionStartTime!.Value, samples[0].Timestamp);
        Assert.Equal(
            options.SessionStartTime!.Value.AddSeconds(100.0 / 42_000_000.0),
            samples[1].Timestamp,
            TimeSpan.FromTicks(1));
        Assert.All(samples, s => Assert.True(s.HasDeviceTimestamp));
    }

    [Fact]
    public async Task ParseAsync_BenchCaptureText_KeepsEveryAnalogAndDigitalReading()
    {
        // Arrange — the first three objects of log_20261031_080550.json, copied verbatim from a
        // bench capture on firmware 3.8.0 (Nyquist 1, two analog channels, 100 Hz, digital
        // sampling on). The first object carries a digital report and no analog reading at all,
        // which is why a log's sample count is one per reading and not one per "ts".
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"meta\":{\"dev\":\"Nyquist 1\",\"sn\":\"7E2815916200E898\",\"tick_hz\":42000000}}",
            "{",
            "\"ts\":2587155960,",
            "\"di\":[{\"ts\":4294965913, \"mask\":65535, \"val\":0}]",
            "}",
            "{",
            "\"ts\":2587575953,",
            "\"di\":[{\"ts\":4294965890, \"mask\":65535, \"val\":0}],",
            "\"ts\":2587575960,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":12},",
            "{\"ch\":1, \"val\":16}",
            "]",
            "}",
            "{",
            "\"ts\":2587995959,",
            "\"di\":[{\"ts\":4294965900, \"mask\":65535, \"val\":0}],",
            "\"ts\":2587995960,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":12},",
            "{\"ch\":1, \"val\":15}",
            "]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2026, 10, 31, 8, 5, 50, DateTimeKind.Utc)
        };

        // Act
        var session = await parser.ParseAsync(stream, "log_20261031_080550.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert — two analog readings and three digital ones, each on its own tick. The two
        // "ai" arrays are samples 1 and 3; the "di" entries sit between and after them, 1383 to
        // 1406 ticks off the trigger stamp their offsets are stated against.
        Assert.Equal(5, samples.Count);
        Assert.Equal(new[] { 12.0, 16.0 }, samples[1].AnalogValues);
        Assert.Equal(new[] { 12.0, 15.0 }, samples[3].AnalogValues);
        Assert.Empty(samples[0].AnalogValues);
        Assert.Empty(samples[2].AnalogValues);
        Assert.Empty(samples[4].AnalogValues);
        Assert.All(samples, s => Assert.Equal(0u, s.DigitalData));

        // The capture ran at 100 Hz, which 420000 ticks of the 42 MHz clock states exactly.
        Assert.Equal(
            samples[1].Timestamp.AddSeconds(0.01),
            samples[3].Timestamp,
            TimeSpan.FromTicks(1));

        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(2, session.DeviceConfig.AnalogPortCount);
        Assert.Equal(42_000_000u, session.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_FirmwareMetaHeader_StatesTheTickRateAndTheDevice()
    {
        // Arrange — the header states a 42 MHz clock, so no fallback guess is needed.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":10,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":1.5}",
            "]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 50_000_000
        };

        // Act
        var session = await parser.ParseAsync(stream, "test.json", options);

        // Assert
        Assert.Equal(42_000_000u, session.TimestampFrequency);
        Assert.Equal(
            global::Daqifi.Core.Device.SdCard.SdCardTimestampSource.LogFile,
            session.TimestampFrequencySource);
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(42_000_000u, session.DeviceConfig.TimestampFrequency);
        Assert.Equal("Nyquist 1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal("7E2815916200E898", session.DeviceConfig.DeviceSerialNumber);
        Assert.Equal(1, session.DeviceConfig.AnalogPortCount);
    }

    [Fact]
    public async Task ParseAsync_FirmwareMetaHeaderBeatsTheDeviceOverride()
    {
        // The file states its own clock, so a connected device cannot overrule it.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":10,",
            "\"ai\":[{\"ch\":0, \"val\":1.5}]",
            "}");

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 1,
            DigitalPortCount: 0,
            TimestampFrequency: 50_000_000,
            DeviceSerialNumber: "OTHER",
            DevicePartNumber: "OTHER",
            FirmwareRevision: "3.8.0",
            CalibrationValues: null);

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.json", options);

        Assert.Equal(42_000_000u, session.TimestampFrequency);
        Assert.Equal(
            global::Daqifi.Core.Device.SdCard.SdCardTimestampSource.LogFile,
            session.TimestampFrequencySource);
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("Nyquist 1", session.DeviceConfig.DevicePartNumber);
        // The file states no firmware revision, so the override still fills that gap.
        Assert.Equal("3.8.0", session.DeviceConfig.FirmwareRevision);
    }

    [Fact]
    public async Task ParseAsync_FirmwareTriggerTimestamp_IsNotASampleOfItsOwn()
    {
        // With digital sampling on, the firmware writes the trigger stamp and the "di" array
        // ahead of the per-sample "ts"/"ai" pairs. The trigger stamp carries no reading, so it
        // must not turn into a sample of its own — three readings here, not four.
        //
        // The entries' offsets are stated as trigger - sampleTime and the firmware pops its
        // digital queue oldest first, so they count down: ticks 490 and 495.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":500,",
            "\"di\":[{\"ts\":10, \"mask\":3, \"val\":1},{\"ts\":5, \"mask\":3, \"val\":2}],",
            "\"ts\":600,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":1.5}",
            "]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(3, samples.Count);
        Assert.Equal(new uint[] { 1u, 2u, 2u }, samples.Select(s => s.DigitalData));
        Assert.Empty(samples[0].AnalogValues);
        Assert.Empty(samples[1].AnalogValues);
        Assert.Equal(1.5, samples[2].AnalogValues[0]);
    }

    [Fact]
    public async Task ParseAsync_FirmwareDigitalState_CarriesToLaterObjects()
    {
        // The firmware writes "di" only when digital samples are queued. A digital line holds
        // its level until the next report, so a later object with no "di" keeps the last state.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"di\":[{\"ts\":5, \"mask\":3, \"val\":3}],",
            "\"ts\":100,",
            "\"ai\":[{\"ch\":0, \"val\":1.0}]",
            "}",
            "{",
            "\"ts\":200,",
            "\"ai\":[{\"ch\":0, \"val\":2.0}]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(3u, samples[0].DigitalData);
        Assert.Equal(3u, samples[1].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_TruncatedFinalObject_IsDropped()
    {
        // A log cut short by a power loss or a failed transfer ends mid-object. Half a sample
        // is not a sample, and the complete samples before it must still arrive.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":100,",
            "\"ai\":[{\"ch\":0, \"val\":1.0}]",
            "}",
            "{",
            "\"ts\":200,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":2.0");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.Equal(1.0, samples[0].AnalogValues[0]);
    }

    [Fact]
    public async Task ParseAsync_FirmwareRawAdcValues_AreScaledByTheOverride()
    {
        // In raw mode (CONF:ADC:USECal 2) the firmware writes integer ADC codes, which the
        // device's calibration turns into volts — the same path the legacy shape uses.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":100,",
            "\"ai\":[",
            "{\"ch\":0, \"val\":0},",
            "{\"ch\":1, \"val\":22}",
            "]",
            "}");

        var overrideConfig = new global::Daqifi.Core.Device.SdCard.SdCardDeviceConfiguration(
            AnalogPortCount: 2,
            DigitalPortCount: 0,
            TimestampFrequency: 0u,
            DeviceSerialNumber: null,
            DevicePartNumber: null,
            FirmwareRevision: null,
            CalibrationValues: new[] { (1.0, 0.0), (1.0, 0.0) },
            Resolution: 65535,
            PortRange: new[] { 10.0, 10.0 },
            InternalScaleM: new[] { 1.0, 1.0 });

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            ConfigurationOverride = overrideConfig
        };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.Equal(0.0, samples[0].AnalogValues[0], precision: 5);
        Assert.Equal(22.0 / 65535.0 * 10.0, samples[0].AnalogValues[1], precision: 5);
    }

    [Fact]
    public async Task ParseAsync_MetaHeaderOnly_ReportsTheMetadataAndNoSamples()
    {
        // A session that stopped before the first sample still says which device wrote it.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(FirmwareMetaHeader);

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Empty(samples);
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("Nyquist 1", session.DeviceConfig.DevicePartNumber);
        Assert.Equal(42_000_000u, session.TimestampFrequency);
        Assert.Equal(0, session.DeviceConfig.AnalogPortCount);
    }

    [Fact]
    public async Task ParseAsync_BraceInsideAStringValue_IsNotObjectStructure()
    {
        // The object reader joins lines by brace depth, so it has to ignore braces that are
        // text inside a string.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"meta\":{\"dev\":\"Nyquist {1}\",\"sn\":\"00\",\"tick_hz\":100}}",
            "{",
            "\"ts\":100,",
            "\"ai\":[{\"ch\":0, \"val\":1.0}]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.NotNull(session.DeviceConfig);
        Assert.Equal("Nyquist {1}", session.DeviceConfig.DevicePartNumber);
        Assert.Equal(100u, session.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_FirmwareObjectStream_CanBeEnumeratedMoreThanOnce()
    {
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":100,",
            "\"ai\":[{\"ch\":0, \"val\":1.0}],",
            "\"ts\":200,",
            "\"ai\":[{\"ch\":0, \"val\":2.0}]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var first = await ToListAsync(session.Samples);
        var second = await ToListAsync(session.Samples);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first.Select(s => (s.Timestamp, s.AnalogValues[0])),
            second.Select(s => (s.Timestamp, s.AnalogValues[0])));
    }

    [Fact]
    public async Task ParseAsync_DigitalOnlyObject_KeepsEveryDigitalReading()
    {
        // The firmware writes a digital report with no "ai" array whenever only digital samples
        // are queued. Each entry of "di" is a reading at a time of its own, so it is a sample of
        // its own with no analog values — the shape the binary parser already produces for a
        // message that carries digital data and no analog data.
        //
        // "di" states each entry's time as StreamTrigStamp - sampleTime (JSON_Encoder.c), so the
        // entry at offset 100 was taken at tick 100 and the one at offset 0 at tick 200.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"meta\":{\"dev\":\"Nyquist 1\",\"sn\":\"00\",\"tick_hz\":100}}",
            "{\"ts\":200,\"di\":[{\"ts\":100,\"mask\":1,\"val\":1},{\"ts\":0,\"mask\":1,\"val\":0}]}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();
        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            SessionStartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var session = await parser.ParseAsync(stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Empty(s.AnalogValues));
        Assert.Equal(1u, samples[0].DigitalData);
        Assert.Equal(0u, samples[1].DigitalData);

        // 100 ticks apart on the 100 Hz clock the header states, so one second.
        Assert.Equal(options.SessionStartTime!.Value, samples[0].Timestamp);
        Assert.Equal(
            options.SessionStartTime!.Value.AddSeconds(1.0),
            samples[1].Timestamp,
            TimeSpan.FromTicks(1));

        Assert.NotNull(session.DeviceConfig);
        Assert.Equal(0, session.DeviceConfig.AnalogPortCount);
    }

    [Fact]
    public async Task ParseAsync_TwoAnalogSamplesInOneObject_EachTakesTheDigitalStateOfItsOwnTime()
    {
        // The two digital entries above, with analog readings at the same two ticks. The state
        // at tick 100 is 1 and at tick 200 it is 0: the last entry's state must not be applied
        // to every analog sample in the object. Each digital reading lands on an analog tick,
        // so that sample carries it instead of repeating it.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            FirmwareMetaHeader,
            "{",
            "\"ts\":200,",
            "\"di\":[{\"ts\":100, \"mask\":1, \"val\":1},{\"ts\":0, \"mask\":1, \"val\":0}],",
            "\"ts\":100,",
            "\"ai\":[{\"ch\":0, \"val\":1.0}],",
            "\"ts\":200,",
            "\"ai\":[{\"ch\":0, \"val\":2.0}]",
            "}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(new[] { 1.0 }, samples[0].AnalogValues);
        Assert.Equal(new[] { 2.0 }, samples[1].AnalogValues);
        Assert.Equal(new uint[] { 1u, 0u }, samples.Select(s => s.DigitalData));
    }

    [Fact]
    public async Task ParseAsync_LegacyAnalogBeforeItsTimestamp_IsStillASample()
    {
        // A JSON object states no order for its fields. The legacy one-line shape with "analog"
        // ahead of "ts" is the same sample as "ts" ahead of "analog".
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"analog\":[1.0],\"digital\":\"01\",\"ts\":100}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Single(samples);
        Assert.Equal(new[] { 1.0 }, samples[0].AnalogValues);
        Assert.Equal(1u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseAsync_DamagedLineLeftAnObjectOpen_LaterObjectsStillParse()
    {
        // One bad byte must cost one object and not the rest of the file. The unclosed brace on
        // the damaged line used to hold the reader's brace depth above zero, so every later
        // object stayed in the buffer and was discarded at the end of the file.
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(
            "{\"ts\":100,\"analog\":[1.0],\"digital\":\"\"}",
            "{bad",
            "{\"ts\":200,\"analog\":[2.0],\"digital\":\"\"}",
            "{\"ts\":300,\"analog\":[3.0],\"digital\":\"\"}");

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(3, samples.Count);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, samples.Select(s => s.AnalogValues[0]));
    }

    /// <summary>
    /// The shapes a log can take, and how many samples each holds. One rule covers all of them:
    /// every reading in an object is a sample, field order is irrelevant except where the
    /// firmware's repeated keys make it the only thing pairing a reading with its time, and a
    /// damaged object costs one object rather than the rest of the file.
    /// </summary>
    public static TheoryData<string, string[], int, int> ObjectShapes() => new()
    {
        {
            "analog only",
            new[] { "{\"ts\":100,\"ai\":[{\"ch\":0, \"val\":1.0}]}" },
            1, 1
        },
        {
            "digital only",
            new[] { "{\"ts\":200,\"di\":[{\"ts\":100, \"mask\":1, \"val\":1}]}" },
            1, 0
        },
        {
            "analog and digital at one tick",
            new[] { "{\"ts\":100,\"di\":[{\"ts\":0, \"mask\":1, \"val\":1}],\"ai\":[{\"ch\":0, \"val\":1.0}]}" },
            1, 1
        },
        {
            "legacy order, timestamp first",
            new[] { "{\"ts\":100,\"analog\":[1.0],\"digital\":\"01\"}" },
            1, 1
        },
        {
            "legacy order, analog first",
            new[] { "{\"analog\":[1.0],\"ts\":100,\"digital\":\"01\"}" },
            1, 1
        },
        {
            "damaged line between good objects",
            new[]
            {
                "{\"ts\":100,\"analog\":[1.0]}",
                "{bad",
                "{\"ts\":200,\"analog\":[2.0]}"
            },
            2, 2
        },
        {
            "object left open across lines, then a good object",
            new[]
            {
                "{",
                "\"ts\":100,",
                "{\"ts\":200,\"analog\":[2.0]}"
            },
            1, 1
        },
        {
            "empty file",
            Array.Empty<string>(),
            0, 0
        }
    };

    [Theory]
    [MemberData(nameof(ObjectShapes))]
    public async Task ParseAsync_ObjectShape_KeepsEveryReading(
        string shape,
        string[] lines,
        int expectedSamples,
        int expectedAnalogSamples)
    {
        await using var stream = SdCardTestJsonFileBuilder.BuildRawJsonFile(lines);

        var parser = new global::Daqifi.Core.Device.SdCard.SdCardJsonFileParser();

        var session = await parser.ParseAsync(stream, "test.json");
        var samples = await ToListAsync(session.Samples);

        Assert.True(
            samples.Count == expectedSamples,
            $"{shape}: expected {expectedSamples} samples, got {samples.Count}");
        var analogSamples = samples.Count(s => s.AnalogValues.Count > 0);
        Assert.True(
            analogSamples == expectedAnalogSamples,
            $"{shape}: expected {expectedAnalogSamples} samples with analog readings, got {analogSamples}");
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
