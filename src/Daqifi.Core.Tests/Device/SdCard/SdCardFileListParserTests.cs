using Daqifi.Core.Device.SdCard;
using System;
using System.Linq;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardFileListParserTests
    {
        // ---- firmware #794: the end-of-listing marker -----------------------

        [Fact]
        public void ParseFileList_WithEndOfListMarker_DoesNotYieldAFile()
        {
            // Arrange -- the marker is not blank, is not an error shape, and its
            // first token is not empty, so every existing filter passes it through.
            var lines = new[] { "Daqifi/data.csv 1024", "__END_OF_LIST__ OK" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert -- one real file, and no phantom named after the marker.
            Assert.Single(result);
            Assert.Equal("data.csv", result[0].FileName);
        }

        [Fact]
        public void ParseFileList_WithFileNamedLikeTheMarker_KeepsTheFile()
        {
            // The marker is matched as a whole token, not as a prefix: a file
            // whose name merely starts with the marker text is still a file, and
            // hiding it would be the same defect as inventing one.
            // No "Daqifi/" prefix on purpose: with one, the line does not start
            // with the marker text at all and the exactness guard is never
            // reached -- the test would pass whether or not the guard exists.
            var lines = new[] { "__END_OF_LIST__notes.csv 12", "__END_OF_LIST__ OK" };

            var result = SdCardFileListParser.ParseFileList(lines);

            Assert.Single(result);
            Assert.Equal("__END_OF_LIST__notes.csv", result[0].FileName);
        }

        [Fact]
        public void GetListingStatus_WithFileNamedLikeTheMarker_IsUnterminated()
        {
            // Same rule from the other side: a filename starting with the marker
            // text must not be read as the listing's terminator.
            var status = SdCardFileListParser.GetListingStatus(
                new[] { "__END_OF_LIST__notes.csv 12" });

            Assert.Equal(SdCardListingStatus.Unterminated, status);
        }

        [Theory]
        [InlineData("__END_OF_LIST__ OK", SdCardListingStatus.Complete)]
        [InlineData("__END_OF_LIST__ INCOMPLETE", SdCardListingStatus.Incomplete)]
        [InlineData("__END_OF_LIST__ FAILED", SdCardListingStatus.Failed)]
        [InlineData("  __END_OF_LIST__ ok  ", SdCardListingStatus.Complete)]
        public void GetListingStatus_ReadsTheMarker(string marker, SdCardListingStatus expected)
        {
            var status = SdCardFileListParser.GetListingStatus(
                new[] { "Daqifi/data.csv 1024", marker });

            Assert.Equal(expected, status);
        }

        [Fact]
        public void GetListingStatus_WithNoMarker_IsUnterminated()
        {
            // Pre-#794 firmware, and the abort case, which deliberately sends none.
            var status = SdCardFileListParser.GetListingStatus(
                new[] { "Daqifi/data.csv 1024" });

            Assert.Equal(SdCardListingStatus.Unterminated, status);
        }

        [Fact]
        public void GetListingStatus_WithUnknownStatusWord_IsNotTreatedAsComplete()
        {
            // A future status word this version does not know still terminated the
            // listing, but its contents cannot be claimed complete.
            var status = SdCardFileListParser.GetListingStatus(
                new[] { "Daqifi/data.csv 1024", "__END_OF_LIST__ SOMETHINGNEW" });

            Assert.Equal(SdCardListingStatus.Incomplete, status);
        }

        [Fact]
        public void GetListingStatus_WithStaleMarkerAhead_TakesTheLast()
        {
            // A marker from a previous, timed-out exchange can lead this reply. The
            // one that ENDS the reply describes the walk that produced it.
            var status = SdCardFileListParser.GetListingStatus(new[]
            {
                "__END_OF_LIST__ FAILED",
                "Daqifi/data.csv 1024",
                "__END_OF_LIST__ OK",
            });

            Assert.Equal(SdCardListingStatus.Complete, status);
        }

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
        public void ParseFileList_WithNullInput_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => SdCardFileListParser.ParseFileList(null!));
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

        [Fact]
        public void ParseFileList_WithSizeToken_ParsesSize()
        {
            // Firmware emits "<path> <size>" per entry. The size is the only thing that lets a
            // later download tell a legitimately empty file from a wedged SD subsystem, so it is
            // retained rather than discarded with the rest of the line (#398 gap 2).
            // Arrange
            var lines = new[] { "Daqifi/log_20240115_103000.bin 4096" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
            Assert.Equal(4096, result[0].SizeInBytes);
        }

        [Fact]
        public void ParseFileList_WithZeroSizeToken_ParsesZeroRatherThanUnknown()
        {
            // A listed 0 is meaningful — it is what makes a marker-only download a legitimate
            // empty file — so it must not collapse into "size unknown".
            // Arrange
            var lines = new[] { "Daqifi/empty.bin 0" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Equal(0, result[0].SizeInBytes);
        }

        [Fact]
        public void ParseFileList_WithoutSizeToken_LeavesSizeUnknown()
        {
            // Arrange
            var lines = new[] { "Daqifi/log_20240115_103000.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Null(result[0].SizeInBytes);
        }

        [Theory]
        [InlineData("Daqifi/data.bin notanumber")]
        [InlineData("Daqifi/data.bin -5")]
        [InlineData("Daqifi/data.bin 4096 extra")]
        [InlineData("Daqifi/data.bin 99999999999999999999")]
        [InlineData("Daqifi/data.bin  ")]
        public void ParseFileList_WithUnparseableSizeToken_LeavesSizeUnknown(string line)
        {
            // Anything that is not a plain non-negative integer is reported as "unknown" rather
            // than guessed at — a wrong size is worse than no size for the empty-file decision.
            // Act
            var result = SdCardFileListParser.ParseFileList(new[] { line });

            // Assert
            Assert.Single(result);
            Assert.Equal("data.bin", result[0].FileName);
            Assert.Null(result[0].SizeInBytes);
        }

        [Fact]
        public void ParseFileList_WithTabSeparatedSizeToken_ParsesSize()
        {
            // Arrange
            var lines = new[] { "Daqifi/data.bin\t512" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Equal(512, result[0].SizeInBytes);
        }

        [Fact]
        public void ParseFileList_WithScpiError_SkipsErrorLines()
        {
            // Arrange - simulates the error response from issue #119
            var lines = new[] { "**ERROR: -200, \"Execution error\"" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithScpiErrorMixedWithFiles_OnlyReturnsFiles()
        {
            // Arrange
            var lines = new[]
            {
                "**ERROR: -200, \"Execution error\"",
                "Daqifi/log_20240115_103000.bin",
                "**ERROR: -100, \"Command error\""
            };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
        }

        [Theory]
        [InlineData("**ERROR: -200, \"Execution error\"")]
        [InlineData("**error: -200")]
        [InlineData("  **ERROR: -100")]
        public void ParseFileList_WithVariousScpiErrorFormats_SkipsAll(string errorLine)
        {
            // Arrange
            var lines = new[] { errorLine };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithJsonLogFileName_ParsesDate()
        {
            // Arrange
            var lines = new[] { "log_20240115_103000.json" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.json", result[0].FileName);
            Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), result[0].CreatedDate);
        }

        [Fact]
        public void ParseFileList_WithCsvLogFileName_ParsesDate()
        {
            // Arrange
            var lines = new[] { "log_20240115_103000.csv" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.csv", result[0].FileName);
            Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), result[0].CreatedDate);
        }

        [Theory]
        [InlineData("log_20240115_103000.bin", "log_20240115_103000.bin")]
        [InlineData("log_20240115_103000.json", "log_20240115_103000.json")]
        [InlineData("log_20240115_103000.csv", "log_20240115_103000.csv")]
        public void ParseFileList_WithMultipleFormats_RetainsCorrectFileName(string input, string expected)
        {
            // Arrange
            var lines = new[] { input };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal(expected, result[0].FileName);
            Assert.NotNull(result[0].CreatedDate);
        }

        [Fact]
        public void ParseFileList_WithPlainErrorLine_SkipsErrorLine()
        {
            // Arrange
            var lines = new[] { "ERROR: -200, \"Execution error\"" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithPlainErrorMixedWithFiles_OnlyReturnsFiles()
        {
            // Arrange
            var lines = new[]
            {
                "ERROR: -200, \"Execution error\"",
                "Daqifi/log_20240115_103000.bin",
                "ERROR: -100, \"Command error\""
            };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Single(result);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
        }

        [Theory]
        [InlineData("ERROR: -200, \"Execution error\"")]
        [InlineData("error: -200")]
        [InlineData("  ERROR: -100")]
        public void ParseFileList_WithVariousPlainErrorFormats_SkipsAll(string errorLine)
        {
            // Arrange
            var lines = new[] { errorLine };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithControlCharacterInFilename_SkipsFile()
        {
            // Arrange
            var lines = new[] { "log_\x01corrupt.bin" };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void ParseFileList_WithControlCharMixedWithValidFiles_OnlyReturnsCleanFiles()
        {
            // Arrange
            var lines = new[]
            {
                "log_20240115_103000.bin",
                "log_\x01corrupt.bin",
                "log_20240116_120000.bin"
            };

            // Act
            var result = SdCardFileListParser.ParseFileList(lines);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("log_20240115_103000.bin", result[0].FileName);
            Assert.Equal("log_20240116_120000.bin", result[1].FileName);
        }
    }
}
