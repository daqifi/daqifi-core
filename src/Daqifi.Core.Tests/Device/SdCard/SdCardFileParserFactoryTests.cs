using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public sealed class SdCardFileParserFactoryTests
{
    [Theory]
    [InlineData("log.bin", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Protobuf)]
    [InlineData("log.BIN", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Protobuf)]
    [InlineData("log.json", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Json)]
    [InlineData("log.JSON", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Json)]
    [InlineData("log.csv", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Csv)]
    [InlineData("log.CSV", global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Csv)]
    public void DetectFormat_ValidExtensions_ReturnsCorrectFormat(string fileName, global::Daqifi.Core.Device.SdCard.SdCardLogFormat expectedFormat)
    {
        // Act
        var format = global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.DetectFormat(fileName);

        // Assert
        Assert.Equal(expectedFormat, format);
    }

    [Theory]
    [InlineData("log.txt")]
    [InlineData("log.dat")]
    [InlineData("log")]
    [InlineData("log.")]
    public void DetectFormat_UnsupportedExtension_ThrowsArgumentException(string fileName)
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.DetectFormat(fileName));

        Assert.Contains("Unsupported file extension", exception.Message);
    }

    [Fact]
    public void DetectFormat_NullFileName_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.DetectFormat(null!));
    }

    [Fact]
    public async Task ParseAsync_JsonFile_RoutesToJsonParser()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0, 2.0 }, "")
        );

        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.ParseAsync(
            stream, "test.json", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(2, samples[0].AnalogValues.Count);
    }

    [Fact]
    public async Task ParseAsync_CsvFile_RoutesToCsvParser()
    {
        // Arrange — real firmware CSV format with 2 channels
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (100u, new[] { 1.0, 2.0 })
        );

        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.ParseAsync(
            stream, "test.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(2, samples[0].AnalogValues.Count);
    }

    [Fact]
    public async Task ParseWithFormatAsync_ExplicitJsonFormat_UsesJsonParser()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 3.0 }, "01")
        );

        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.ParseWithFormatAsync(
            stream,
            "anyname.dat",  // Extension doesn't matter when format is explicit
            global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Json,
            options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(3.0, samples[0].AnalogValues[0]);
        Assert.Equal(0x01u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseWithFormatAsync_ExplicitCsvFormat_UsesCsvParser()
    {
        // Arrange — real firmware CSV format (no digital column; DigitalData is always 0)
        await using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "TestDevice", "SN001", 100u,
            (100u, new[] { 5.0 })
        );

        var options = new global::Daqifi.Core.Device.SdCard.SdCardParseOptions
        {
            FallbackTimestampFrequency = 100
        };

        // Act
        var session = await global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.ParseWithFormatAsync(
            stream,
            "anyname.dat",  // Extension doesn't matter when format is explicit
            global::Daqifi.Core.Device.SdCard.SdCardLogFormat.Csv,
            options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        Assert.Single(samples);
        Assert.Equal(5.0, samples[0].AnalogValues[0]);
        // Real firmware CSV has no digital column — always 0
        Assert.Equal(0u, samples[0].DigitalData);
    }

    [Fact]
    public async Task ParseWithFormatAsync_InvalidFormat_ThrowsArgumentException()
    {
        // Arrange
        await using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (100u, new[] { 1.0 }, "")
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await global::Daqifi.Core.Device.SdCard.SdCardFileParserFactory.ParseWithFormatAsync(
                stream,
                "test.json",
                (global::Daqifi.Core.Device.SdCard.SdCardLogFormat)999,
                null));
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
