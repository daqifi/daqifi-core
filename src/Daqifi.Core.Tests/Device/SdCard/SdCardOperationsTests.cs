using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardOperationsTests
    {
        [Fact]
        public async Task GetSdCardFilesAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.GetSdCardFilesAsync());
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log_20240115_103000.bin" };
            device.Connect();

            // Act
            await device.GetSdCardFilesAsync();

            // Assert - verify SD interface prep and file list commands were sent via setup action
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands); // DisableNetworkLan (PrepareSdInterface)
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sentCommands); // EnableStorageSd (PrepareSdInterface)
            Assert.Contains("SYSTem:STORage:SD:LIST?", sentCommands); // GetSdFileList
        }

        [Fact]
        public async Task GetSdCardFilesAsync_ParsesResponseCorrectly()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/log_20240115_103000.bin",
                "Daqifi/data.bin"
            };
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert
            Assert.Equal(2, files.Count);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
            Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), files[0].CreatedDate);
            Assert.Equal("data.bin", files[1].FileName);
            Assert.Null(files[1].CreatedDate);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_FilenamesStartingWithErrorAreNotMisclassified()
        {
            // Regression for #190 covering BOTH bug locations:
            //   - IsNonResultLine in DaqifiStreamingDevice (the LIST? response classifier)
            //   - SdCardFileListParser.ParseFileList (the per-line parser; bare "ERROR" check)
            // Pre-fix, both used a bare "ERROR" StartsWith check that
            // false-positived on legit SD filenames. Tightened to require
            // ERROR followed by ":"/" "/"!"/tab/end-of-line.
            //
            // Cover both path shapes the firmware may emit:
            //   - prefixed: "Daqifi/error_log.csv"
            //   - bare: "error_log.csv" (no Daqifi/ prefix)
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/error_log.csv",
                "Daqifi/Errors_summary.bin",
                "error_log.csv",
                "Errors_summary.bin",
                "ERROR_archive.bin",
                "Daqifi/normal.bin",
            };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            var names = files.Select(f => f.FileName).ToList();
            Assert.Contains("error_log.csv", names);
            Assert.Contains("Errors_summary.bin", names);
            Assert.Contains("ERROR_archive.bin", names);
            Assert.Contains("normal.bin", names);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_OnlyErrorPrefixedFilenames_AllSurvive()
        {
            // Edge case explicitly called out by Qodo on PR #195: a
            // listing consisting SOLELY of error*-prefixed filenames
            // (no normal.bin to act as a sanity anchor) must round-trip
            // every entry. Pre-fix, the entire response would have parsed
            // as zero files.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "error_log.csv",
                "errors.bin",
                "Erroneous_data.bin",
            };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Equal(3, files.Count);
            var names = files.Select(f => f.FileName).ToList();
            Assert.Contains("error_log.csv", names);
            Assert.Contains("errors.bin", names);
            Assert.Contains("Erroneous_data.bin", names);
        }

        [Theory]
        [InlineData("**ERROR: -200, Execution error")]
        [InlineData("**Error: bad")]
        [InlineData("ERROR: -100, Bad command")]
        [InlineData("Error !! Generic firmware status")]
        [InlineData("Error!! No space firmware status")]
        [InlineData("ERROR")]
        [InlineData("error\tsomething")]
        public async Task GetSdCardFilesAsync_RealErrorLinesStillSkipped(string errorLine)
        {
            // Confirm the tightening didn't go too far — real error
            // lines still classify as non-result and don't end up
            // misinterpreted as filenames.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/normal.bin",
                errorLine,
            };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Single(files);
            Assert.Equal("normal.bin", files[0].FileName);
        }

        [Theory]
        [InlineData("error!log.bin")]
        [InlineData("Daqifi/error!log.bin")]
        [InlineData("Erroneous!data.bin")]
        public async Task GetSdCardFilesAsync_FilenamesWithSingleBangSurvive(string filename)
        {
            // Regression: a single '!' immediately after "error" is ambiguous
            // (could be a filename like "error!log.bin"). The classifier must
            // require '!!' to treat as an error/status line so legitimate
            // filenames aren't dropped from listings. Filename validation
            // already permits '!' in SD filenames.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/normal.bin",
                filename,
            };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            var names = files.Select(f => f.FileName).ToList();
            Assert.Equal(2, names.Count);
            Assert.Contains("normal.bin", names);
            // Mirror production normalization: strip the Daqifi/ prefix then keep
            // the basename. Split on '/' explicitly (not Path.GetFileName) — the
            // device protocol uses forward slashes, and Path.GetFileName treats
            // '\\' as a separator on Windows but not on Linux/macOS, which would
            // make this expectation OS-dependent if a future case used '\\'.
            const string daqifiPrefix = "Daqifi/";
            var expected = filename.StartsWith(daqifiPrefix, StringComparison.OrdinalIgnoreCase)
                ? filename.Substring(daqifiPrefix.Length)
                : filename;
            var lastSlash = expected.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                expected = expected.Substring(lastSlash + 1);
            }
            Assert.Contains(expected, names);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_RestoresLanInterface()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/test.bin" };
            device.Connect();

            // Act
            await device.GetSdCardFilesAsync();

            // Assert - verify LAN interface restoration commands were sent
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands); // DisableStorageSd (PrepareLanInterface)
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan (PrepareLanInterface)
        }

        [Fact]
        public async Task GetSdCardFilesAsync_UpdatesSdCardFilesProperty()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/test.bin" };
            device.Connect();

            // Act
            await device.GetSdCardFilesAsync();

            // Assert
            Assert.Single(device.SdCardFiles);
            Assert.Equal("test.bin", device.SdCardFiles[0].FileName);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_HonorsCancellationDuringSettleDelay()
        {
            // Regression for #221: the SD interface settle wait used Thread.Sleep,
            // which ignored the CancellationToken. After the fix the wait is
            // await Task.Delay(..., ct), so cancelling while the operation is
            // suspended in the delay must propagate as OperationCanceledException.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/test.bin" };
            device.Connect();

            using var cts = new CancellationTokenSource();

            // The sync portion of GetSdCardFilesAsync runs through the setup
            // lambda's PrepareSdInterface and suspends at Task.Delay(..., ct).
            // Once it returns a pending task, we cancel synchronously: Task.Delay
            // observes the cancellation immediately. Under the old Thread.Sleep
            // code the cancel would be ignored and the call would complete
            // normally — no OperationCanceledException would be thrown.
            var opTask = device.GetSdCardFilesAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opTask);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_HonorsCancellationDuringSettleDelay()
        {
            // Regression for #221 — symmetric with GetSdCardFilesAsync above.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/other.bin" };
            device.Connect();

            using var cts = new CancellationTokenSource();
            var opTask = device.DeleteSdCardFileAsync("data.bin", cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opTask);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_SendsCorrectCommandSequence()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("mylog.bin");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(6, sentCommands.Count);
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[1]);
            Assert.Equal("SYSTem:STReam:INTerface 2", sentCommands[2]);
            Assert.Equal("SYSTem:STORage:SD:FILE \"mylog.bin\"", sentCommands[3]);
            Assert.Equal("SYSTem:STReam:FORmat 0", sentCommands[4]);
            Assert.Equal("SYSTem:StartStreamData 100", sentCommands[5]);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_OverNonUsbConnection_ThrowsInvalidOperationException()
        {
            // Arrange — use a device that reports IsUsbConnection = false
            var device = new TestableNonUsbStreamingDevice("TestDevice");
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.StartSdCardLoggingAsync("test.bin"));
            Assert.Contains("USB", ex.Message);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithCustomFileName_UsesProvidedName()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("custom_data.bin");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:FILE \"custom_data.bin\"", sentCommands);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithNullFileName_GeneratesTimestampedName()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync();

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:FILE"));
            Assert.NotNull(loggingCommand);
            Assert.Contains("log_", loggingCommand);
            Assert.Contains(".bin", loggingCommand);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_SetsIsLoggingToTrue()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("test.bin");

            // Assert
            Assert.True(device.IsLoggingToSdCard);
            Assert.True(device.IsStreaming);
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");
            device.SentMessages.Clear();

            // Act
            await device.StopSdCardLoggingAsync();

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(4, sentCommands.Count);
            Assert.Equal("SYSTem:StopStreamData", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 0", sentCommands[1]);
            Assert.Equal("SYSTem:STReam:INTerface 0", sentCommands[2]); // Restore USB
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands[3]); // Re-enable LAN
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_SendsStopCommandEvenWhenIsStreamingIsFalse()
        {
            // Arrange - simulate stale IsStreaming state (see issue #118)
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");
            device.StopStreaming(); // Sets IsStreaming = false
            device.SentMessages.Clear();

            // Act
            await device.StopSdCardLoggingAsync();

            // Assert - stop command should still be sent defensively
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_SetsIsLoggingToFalse()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            // Act
            await device.StopSdCardLoggingAsync();

            // Assert
            Assert.False(device.IsLoggingToSdCard);
            Assert.False(device.IsStreaming);
        }

        [Fact]
        public void IsLoggingToSdCard_DefaultsToFalse()
        {
            // Arrange & Act
            var device = new DaqifiStreamingDevice("TestDevice");

            // Assert
            Assert.False(device.IsLoggingToSdCard);
        }

        [Fact]
        public void SdCardFiles_DefaultsToEmpty()
        {
            // Arrange & Act
            var device = new DaqifiStreamingDevice("TestDevice");

            // Assert
            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithEmptyFileName_GeneratesTimestampedName()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:FILE"));
            Assert.NotNull(loggingCommand);
            Assert.Contains("log_", loggingCommand);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithWhitespaceFileName_GeneratesTimestampedName()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("   ");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:FILE"));
            Assert.NotNull(loggingCommand);
            Assert.Contains("log_", loggingCommand);
        }

        [Theory]
        [InlineData("file\".bin")]
        [InlineData("file\n.bin")]
        [InlineData("file\r.bin")]
        [InlineData("file;.bin")]
        public async Task StartSdCardLoggingAsync_WithInvalidCharacters_ThrowsArgumentException(string fileName)
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => device.StartSdCardLoggingAsync(fileName));
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithJsonFormat_SendsJsonFormatCommand()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("mylog.json", format: SdCardLogFormat.Json);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(6, sentCommands.Count);
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[1]);
            Assert.Equal("SYSTem:STReam:INTerface 2", sentCommands[2]);
            Assert.Equal("SYSTem:STORage:SD:FILE \"mylog.json\"", sentCommands[3]);
            Assert.Equal("SYSTem:STReam:FORmat 1", sentCommands[4]);
            Assert.Equal("SYSTem:StartStreamData 100", sentCommands[5]);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithCsvFormat_SendsCsvFormatCommand()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("mylog.csv", format: SdCardLogFormat.Csv);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(6, sentCommands.Count);
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[1]);
            Assert.Equal("SYSTem:STReam:INTerface 2", sentCommands[2]);
            Assert.Equal("SYSTem:STORage:SD:FILE \"mylog.csv\"", sentCommands[3]);
            Assert.Equal("SYSTem:STReam:FORmat 2", sentCommands[4]);
            Assert.Equal("SYSTem:StartStreamData 100", sentCommands[5]);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithNullFileName_JsonFormat_GeneratesJsonExtension()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync(null, format: SdCardLogFormat.Json);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:FILE"));
            Assert.NotNull(loggingCommand);
            Assert.Contains("log_", loggingCommand);
            Assert.Contains(".json", loggingCommand);
            Assert.DoesNotContain(".bin", loggingCommand);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithNullFileName_CsvFormat_GeneratesCsvExtension()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync(null, format: SdCardLogFormat.Csv);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:FILE"));
            Assert.NotNull(loggingCommand);
            Assert.Contains("log_", loggingCommand);
            Assert.Contains(".csv", loggingCommand);
            Assert.DoesNotContain(".bin", loggingCommand);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithProtobufFormat_SendsProtobufFormatCommand()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act — explicitly specifying Protobuf format should behave identically to the default
            await device.StartSdCardLoggingAsync("mylog.bin", format: SdCardLogFormat.Protobuf);


            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STReam:FORmat 0", sentCommands);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.StartSdCardLoggingAsync());
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.StopSdCardLoggingAsync());
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/other.bin" };
            device.Connect();

            // Act
            await device.DeleteSdCardFileAsync("data.bin");

            // Assert - verify SD interface prep, delete, and file list refresh via setup action
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands); // DisableNetworkLan (PrepareSdInterface)
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sentCommands); // EnableStorageSd (PrepareSdInterface)
            Assert.Contains("SYSTem:STORage:SD:DELete \"data.bin\"", sentCommands); // Delete
            Assert.Contains("SYSTem:STORage:SD:LIST?", sentCommands); // File list refresh
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_UpdatesSdCardFilesProperty()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/remaining.bin" };
            device.Connect();

            // Act
            await device.DeleteSdCardFileAsync("data.bin");

            // Assert
            Assert.Single(device.SdCardFiles);
            Assert.Equal("remaining.bin", device.SdCardFiles[0].FileName);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_RestoresLanInterface()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>();
            device.Connect();

            // Act
            await device.DeleteSdCardFileAsync("data.bin");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands); // DisableStorageSd (PrepareLanInterface)
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan (PrepareLanInterface)
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.DeleteSdCardFileAsync("data.bin"));
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WhenLogging_Throws()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.DeleteSdCardFileAsync("data.bin"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task DeleteSdCardFileAsync_WithNullOrEmptyFileName_ThrowsArgumentException(string? fileName)
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => device.DeleteSdCardFileAsync(fileName!));
        }

        [Theory]
        [InlineData("file\".bin")]
        [InlineData("file\n.bin")]
        [InlineData("file\r.bin")]
        [InlineData("file;.bin")]
        public async Task DeleteSdCardFileAsync_WithInvalidCharacters_ThrowsArgumentException(string fileName)
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => device.DeleteSdCardFileAsync(fileName));
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithScpiError_RetriesAndReturnsFiles()
        {
            // Arrange - simulate error on first call, success on second
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.ResponseSequence.Enqueue(new List<string> { "Daqifi/log_20240115_103000.bin" });
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert - should have retried and returned files from second attempt
            Assert.Single(files);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithPersistentScpiError_ThrowsSdCardOperationException()
        {
            // Arrange - simulate persistent bare SCPI error (card busy / timeout territory).
            // Previous behavior returned an empty list, which made real failures look
            // identical to "directory is empty". Issue #181 surfaces this as a typed
            // exception so callers can show actionable detail.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.GetSdCardFilesAsync());
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
            Assert.Contains("**ERROR", ex.LastScpiError);
            Assert.NotEmpty(ex.RawDeviceResponse);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithNoSdCardDetected_ThrowsSdCardNotPresentException()
        {
            // Arrange - matches the firmware response when no SD card is installed:
            // \r\nError !! No SD Card Detected\r\n + **ERROR: -200, "Execution error"
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                "Error !! No SD Card Detected",
                "**ERROR: -200, \"Execution error\""
            };
            device.ResponseSequence.Enqueue(response);
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardNotPresentException>(
                () => device.GetSdCardFilesAsync());
            Assert.Contains("**ERROR", ex.LastScpiError);
            Assert.NotEmpty(ex.RawDeviceResponse);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithFilesystemError_ThrowsSdCardFilesystemException()
        {
            // Arrange - matches the firmware response when the directory cannot be opened
            // (corrupt FS, unformatted card, etc): "[Error:N]Failed to open directory ..."
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                "[Error:3]Failed to open directory /Daqifi"
            };
            device.ResponseSequence.Enqueue(response);
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            // Act & Assert
            // Note: there is no SCPI error line in this response, but there are also
            // no file lines, so the classifier treats it as a filesystem error.
            var ex = await Assert.ThrowsAsync<SdCardFilesystemException>(
                () => device.GetSdCardFilesAsync());
            Assert.Contains("Failed to open directory", ex.DeviceMessage);
            Assert.Contains("Failed to open directory", ex.Message);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithFilesystemErrorAndScpi_ThrowsSdCardFilesystemException()
        {
            // Arrange - filesystem error accompanied by an SCPI error line
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                "[Error:3]Failed to open directory /Daqifi",
                "**ERROR: -200, \"Execution error\""
            };
            device.ResponseSequence.Enqueue(response);
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardFilesystemException>(
                () => device.GetSdCardFilesAsync());
            Assert.Contains("Failed to open directory", ex.DeviceMessage);
            Assert.Contains("**ERROR", ex.LastScpiError);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithFilesAndInterleavedError_ReturnsFiles()
        {
            // Arrange - response contains both files and an SCPI error line.
            // The presence of any error line triggers a retry (existing behavior),
            // so we enqueue the same mixed payload twice. After all retries exhaust,
            // because file lines are present, we still hand off to the parser and
            // ignore the stray error line — issue #181 keeps this behavior intact.
            var mixed = new List<string>
            {
                "Daqifi/log_20240115_103000.bin",
                "**ERROR: -200, \"Execution error\""
            };
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string>(mixed));
            device.ResponseSequence.Enqueue(new List<string>(mixed));
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert
            Assert.Single(files);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithEmptyDirectory_ReturnsEmptyList()
        {
            // Arrange - device returns no lines (empty directory, no errors). This is
            // the legitimate "0 files" case and must keep its existing behavior.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string>());
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert
            Assert.Empty(files);
            Assert.Equal(1, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_LastScpiError_ContainsOnlyScpiFormattedLine()
        {
            // Arrange — firmware emits both "Error !! ..." status text AND a SCPI error.
            // LastScpiError must only carry the SCPI-formatted line so callers can
            // parse it; the status text is preserved in RawDeviceResponse.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                "Error !! No SD Card Detected",
                "**ERROR: -200, \"Execution error\""
            };
            device.ResponseSequence.Enqueue(response);
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            // Act
            var ex = await Assert.ThrowsAsync<SdCardNotPresentException>(
                () => device.GetSdCardFilesAsync());

            // Assert — LastScpiError must be the SCPI line, never the firmware text
            Assert.NotNull(ex.LastScpiError);
            Assert.StartsWith("**ERROR", ex.LastScpiError);
            Assert.DoesNotContain("Error !!", ex.LastScpiError);
            Assert.Contains("Error !! No SD Card Detected", ex.RawDeviceResponse);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithFirmwareTextOnly_ThrowsWithNullLastScpiError()
        {
            // Arrange — defensive: hypothetical firmware response with status text
            // but no SCPI error line. Shouldn't happen for known paths, but the
            // classifier must not silently return an empty list.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string> { "Error !! Some unfamiliar firmware error" };
            device.ResponseSequence.Enqueue(response);
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            // Act
            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.GetSdCardFilesAsync());

            // Assert
            Assert.Null(ex.LastScpiError);
            Assert.Contains("Some unfamiliar firmware error", ex.Message);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_OnError_StillRestoresLanInterface()
        {
            // Arrange - persistent error path must still restore the LAN interface
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "Error !! No SD Card Detected", "**ERROR: -200" });
            device.ResponseSequence.Enqueue(new List<string> { "Error !! No SD Card Detected", "**ERROR: -200" });
            device.Connect();

            // Act
            await Assert.ThrowsAsync<SdCardNotPresentException>(
                () => device.GetSdCardFilesAsync());

            // Assert - LAN restore commands must have been sent even though we threw
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WithScpiError_RetriesAndReturnsFiles()
        {
            // Arrange
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.ResponseSequence.Enqueue(new List<string> { "Daqifi/remaining.bin" });
            device.Connect();

            // Act
            await device.DeleteSdCardFileAsync("data.bin");

            // Assert
            Assert.Single(device.SdCardFiles);
            Assert.Equal("remaining.bin", device.SdCardFiles[0].FileName);
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithNoError_DoesNotRetry()
        {
            // Arrange
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "Daqifi/test.bin" });
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert
            Assert.Single(files);
            Assert.Equal(1, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task FormatSdCardAsync_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.FormatSdCardAsync();

            // Assert — defensive stop is always sent first (issue #118)
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(3, sentCommands.Count);
            Assert.Equal("SYSTem:StopStreamData", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[1]);
            Assert.Equal("SYSTem:STORage:SD:FORmat", sentCommands[2]);
        }

        [Fact]
        public async Task FormatSdCardAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.FormatSdCardAsync());
        }

        [Fact]
        public async Task FormatSdCardAsync_WhenLogging_Throws()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.FormatSdCardAsync());
        }

        #region Defensive stop tests (issue #118)

        [Fact]
        public async Task GetSdCardFilesAsync_WhenNotStreaming_StillSendsStopCommand()
        {
            // Arrange — device is connected but NOT streaming
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/test.bin" };
            device.Connect();
            Assert.False(device.IsStreaming);

            // Act
            await device.GetSdCardFilesAsync();

            // Assert — stop command should still be sent defensively
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WhenNotStreaming_StillSendsStopCommand()
        {
            // Arrange — device is connected but NOT streaming
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/other.bin" };
            device.Connect();
            Assert.False(device.IsStreaming);

            // Act
            await device.DeleteSdCardFileAsync("data.bin");

            // Assert — stop command should still be sent defensively
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenNotStreaming_StillSendsStopCommand()
        {
            // Arrange — device is connected but NOT streaming
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = new byte[] { 0x01 };
            device.Connect();
            Assert.False(device.IsStreaming);

            using var destinationStream = new MemoryStream();

            // Act
            await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert — stop command should still be sent defensively
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        [Fact]
        public async Task FormatSdCardAsync_WhenNotStreaming_StillSendsStopCommand()
        {
            // Arrange — device is connected but NOT streaming
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            Assert.False(device.IsStreaming);

            // Act
            await device.FormatSdCardAsync();

            // Assert — stop command should still be sent defensively
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        #endregion

        #region GetSdCardStorageAsync Tests

        [Fact]
        public async Task GetSdCardStorageAsync_WhenDisconnected_Throws()
        {
            var device = new DaqifiStreamingDevice("TestDevice");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.GetSdCardStorageAsync());
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WhenConnected_SendsCorrectCommands()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "1024,4096" };
            device.Connect();

            await device.GetSdCardStorageAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands); // PrepareSdInterface
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sentCommands);       // PrepareSdInterface
            Assert.Contains("SYSTem:STORage:SD:SPACe?", sentCommands);         // GetSdSpace
        }

        [Fact]
        public async Task GetSdCardStorageAsync_ParsesResponseCorrectly()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "1048576000,2097152000" };
            device.Connect();

            var storage = await device.GetSdCardStorageAsync();

            Assert.Equal(1_048_576_000L, storage.FreeBytes);
            Assert.Equal(2_097_152_000L, storage.TotalBytes);
            Assert.Equal(1_048_576_000L, storage.UsedBytes);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_RestoresLanInterface()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "1024,4096" };
            device.Connect();

            await device.GetSdCardStorageAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);       // PrepareLanInterface
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // PrepareLanInterface
        }

        [Fact]
        public async Task GetSdCardStorageAsync_DefensivelySendsStopStreaming()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "1024,4096" };
            device.Connect();
            Assert.False(device.IsStreaming);

            await device.GetSdCardStorageAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:StopStreamData", sentCommands);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WithScpiError_RetriesAndReturnsStorage()
        {
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.ResponseSequence.Enqueue(new List<string> { "1024,4096" });
            device.Connect();

            var storage = await device.GetSdCardStorageAsync();

            Assert.Equal(1024L, storage.FreeBytes);
            Assert.Equal(4096L, storage.TotalBytes);
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WithPersistentScpiError_ThrowsSdCardOperationException()
        {
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -200, \"Execution error\"" });
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.GetSdCardStorageAsync());
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
            Assert.Contains("**ERROR", ex.LastScpiError);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WithNoSdCardDetected_ThrowsSdCardNotPresentException()
        {
            // The "No SD Card Detected" marker is non-transient, so the method must
            // short-circuit on the first attempt instead of retrying.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string>
            {
                "Error !! No SD Card Detected",
                "**ERROR: -200, \"Execution error\""
            });
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardNotPresentException>(
                () => device.GetSdCardStorageAsync());
            Assert.Contains("**ERROR", ex.LastScpiError);
            Assert.Equal(1, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_OnError_StillRestoresLanInterface()
        {
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "Error !! No SD Card Detected", "**ERROR: -200" });
            device.Connect();

            await Assert.ThrowsAsync<SdCardNotPresentException>(
                () => device.GetSdCardStorageAsync());

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WhenLogging_Throws()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.GetSdCardStorageAsync());
        }

        #endregion

        #region DownloadSdCardFileAsync Tests

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");
            using var stream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.DownloadSdCardFileAsync("test.bin", stream));
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenNotSerialTransport_Throws()
        {
            // Arrange — use a device that reports IsUsbConnection = false
            var device = new TestableNonUsbStreamingDevice("TestDevice");
            device.Connect();
            using var stream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.DownloadSdCardFileAsync("test.bin", stream));
            Assert.Contains("USB", ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task DownloadSdCardFileAsync_WithNullOrEmptyFileName_ThrowsArgumentException(string? fileName)
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.Connect();
            using var stream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => device.DownloadSdCardFileAsync(fileName!, stream));
        }

        [Theory]
        [InlineData("file\".bin")]
        [InlineData("file\n.bin")]
        [InlineData("file;.bin")]
        public async Task DownloadSdCardFileAsync_WithInvalidCharacters_ThrowsArgumentException(string fileName)
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.Connect();
            using var stream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(
                () => device.DownloadSdCardFileAsync(fileName, stream));
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenLogging_Throws()
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");
            using var stream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.DownloadSdCardFileAsync("data.bin", stream));
            Assert.Contains("logging", ex.Message);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_SendsCorrectCommands()
        {
            // Arrange
            var fileData = new byte[] { 0x01, 0x02, 0x03 };
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = fileData;
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act
            await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands); // PrepareSdInterface
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sentCommands); // PrepareSdInterface
            Assert.Contains("SYSTem:STORage:SD:GET \"data.bin\"", sentCommands); // GetSdFile
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WritesFileDataToDestination()
        {
            // Arrange
            var fileData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = fileData;
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act
            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert
            Assert.Equal(fileData, destinationStream.ToArray());
            Assert.Equal("data.bin", result.FileName);
            Assert.Equal(fileData.Length, result.FileSize);
            Assert.True(result.Duration > TimeSpan.Zero);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_RestoresLanInterface()
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = new byte[] { 0x01 };
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act
            await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert — LAN interface should be restored after download
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands); // DisableStorageSd
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_ToTempFile_ReturnsFilePath()
        {
            // Arrange
            var fileData = new byte[] { 0x01, 0x02, 0x03 };
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = fileData;
            device.Connect();

            // Act
            var result = await device.DownloadSdCardFileAsync("data.bin");

            // Assert
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
            Assert.Equal(fileData, await File.ReadAllBytesAsync(result.FilePath));
            Assert.Equal("data.bin", result.FileName);

            // Cleanup
            File.Delete(result.FilePath);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_StopsStreamingBeforeDownload()
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = new byte[] { 0x01 };
            device.Connect();
            device.StartStreaming();
            Assert.True(device.IsStreaming);
            device.SentMessages.Clear();

            using var destinationStream = new MemoryStream();

            // Act
            await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert — stop streaming command should be sent before the download commands
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal("SYSTem:StopStreamData", sentCommands[0]);
        }

        #endregion

        /// <summary>
        /// A testable device that returns different responses on successive calls to
        /// ExecuteTextCommandAsync, allowing tests to verify retry behavior.
        /// </summary>
        private class RetryableSdCardStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public Queue<List<string>> ResponseSequence { get; } = new();
            public int ExecuteTextCommandCallCount { get; private set; }

            public RetryableSdCardStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                setupAction();
                ExecuteTextCommandCallCount++;
                var response = ResponseSequence.Count > 0
                    ? ResponseSequence.Dequeue()
                    : new List<string>();
                return Task.FromResult<IReadOnlyList<string>>(response);
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                ExecuteTextCommandCallCount++;
                var response = ResponseSequence.Count > 0
                    ? ResponseSequence.Dequeue()
                    : new List<string>();
                return response;
            }
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that captures sent messages
        /// and returns canned text responses for ExecuteTextCommandAsync.
        /// </summary>
        private class TestableSdCardStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public List<string> CannedTextResponse { get; set; } = new();

            /// <summary>
            /// Simulates a USB connection so SD card operations are allowed.
            /// </summary>
            public override bool IsUsbConnection => true;

            public TestableSdCardStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                // Execute the setup action so we can capture the SCPI commands
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse);
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                return CannedTextResponse;
            }
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that simulates a USB connection
        /// so DownloadSdCardFileAsync passes the USB transport check.
        /// </summary>
        private class TestableDownloadDevice : DaqifiStreamingDevice
        {
            private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public List<string> CannedTextResponse { get; set; } = new();
            public byte[] CannedFileData { get; set; } = Array.Empty<byte>();

            public TestableDownloadDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override bool IsUsbConnection => true;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse);
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                return CannedTextResponse;
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                // Build a stream with file data + EOF marker
                var data = new byte[CannedFileData.Length + EofMarker.Length];
                Array.Copy(CannedFileData, 0, data, 0, CannedFileData.Length);
                Array.Copy(EofMarker, 0, data, CannedFileData.Length, EofMarker.Length);

                using var fakeStream = new MemoryStream(data);
                await rawAction(fakeStream, cancellationToken);
            }
        }
        /// <summary>
        /// A testable device that reports IsUsbConnection = false to verify
        /// that SD card operations reject non-USB connections.
        /// </summary>
        private class TestableNonUsbStreamingDevice : DaqifiStreamingDevice
        {
            public TestableNonUsbStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override bool IsUsbConnection => false;

            public override void Send<T>(IOutboundMessage<T> message)
            {
            }
        }
    }
}
