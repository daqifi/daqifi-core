using Daqifi.Core.Device.SdCard;
using System;
using System.Linq;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardFileListParserTests
    {
        [Fact]
        public void ParseFileList_WithValidFiles_ReturnsCorrectCount()
        {
            // Arrange
            var lines = new[] { "file1.bin", "file2.bin", "file3.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void ParseFileList_WithDaqifiPrefix_StripsPrefix()
        {
            // Arrange
            var lines = new[] { "Daqifi/log_20240115_103000.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
        }

        [Fact]
        public void ParseFileList_WithLogFileName_ParsesDate()
        {
            // Arrange
            var lines = new[] { "log_20240115_103000.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.NotNull(result[0].CreatedDate);
            Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), result[0].CreatedDate);
        }

        [Fact]
        public void ParseFileList_WithNonLogFile_SetsNullDate()
        {
            // Arrange
            var lines = new[] { "data.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Null(result[0].CreatedDate);
        }

        [Fact]
        public void ParseFileList_WithEmptyLines_SkipsThem()
        {
            // Arrange
            var lines = new[] { "file1.bin", "", "  ", "file2.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("file1.bin", result[0].FileName);
            Assert.Equal("file2.bin", result[1].FileName);
        }

        [Fact]
        public void ParseFileList_WithEmptyInput_ReturnsEmpty()
        {
            // Arrange
            var lines = Array.Empty<string>();

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithNestedPath_ExtractsFileName()
        {
            // Arrange
            var lines = new[] { "Daqifi/subdir/log_20240115_103000.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
        }

        [Fact]
        public void ParseFileList_WithInvalidDateFormat_SetsNullDate()
        {
            // Arrange
            var lines = new[] { "log_invalid.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_invalid.bin", result[0].FileName);
            Assert.Null(result[0].CreatedDate);
        }
    }
}
