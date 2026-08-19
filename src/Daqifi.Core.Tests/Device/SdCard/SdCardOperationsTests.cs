using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
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
        public async Task GetSdCardFilesAsync_WhenCancelledDuringTheSettleDelay_StillRestoresTheLanInterface()
        {
            // By the time the settle wait is cancelled the prepare phase has already switched the
            // bus, so the exchange unwinds with the device sitting on the SD card. The restore has
            // to happen anyway — which is why it is a phase the exchange owns and runs from its own
            // try/finally, rather than a step tacked on to the end of a successful exchange (#407).
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/test.bin" };
            device.Connect();

            using var cts = new CancellationTokenSource();
            var opTask = device.GetSdCardFilesAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opTask);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);       // DisableStorageSd
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan
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
        public async Task StartSdCardLoggingAsync_WithCustomFileName_ReturnsSessionWithThatName()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            var session = await device.StartSdCardLoggingSessionAsync("custom_data.bin", format: SdCardLogFormat.Protobuf);

            // Assert: the returned name is exactly what was sent to the device.
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal("custom_data.bin", session.FileName);
            Assert.Equal(SdCardLogFormat.Protobuf, session.Format);
            Assert.Contains($"SYSTem:STORage:SD:FILE \"{session.FileName}\"", sentCommands);
        }

        [Fact]
        public async Task StartSdCardLoggingAsync_WithNullFileName_ReturnsGeneratedNameSentToDevice()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            var session = await device.StartSdCardLoggingSessionAsync(format: SdCardLogFormat.Json);

            // Assert: the auto-generated name the caller receives is the one that reached the device,
            // so consumers no longer have to re-derive Core's naming convention.
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.StartsWith("log_", session.FileName);
            Assert.EndsWith(".json", session.FileName);
            Assert.Equal(SdCardLogFormat.Json, session.Format);
            Assert.Contains($"SYSTem:STORage:SD:FILE \"{session.FileName}\"", sentCommands);
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
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.StartSdCardLoggingAsync());
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
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
        public async Task DeleteSdCardFileAsync_WithIncompleteListing_ThrowsAndKeepsTheCache()
        {
            // The refresh after a DELETE is what a caller consults next to
            // decide whether the file is gone, so a listing the device itself
            // called INCOMPLETE must not become that answer -- every absence in
            // a short list would look confirmed.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/remaining.bin 12",
                "__END_OF_LIST__ INCOMPLETE",
            };
            device.Connect();

            await Assert.ThrowsAsync<SdCardListIncompleteException>(
                () => device.DeleteSdCardFileAsync("data.bin"));

            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WithNoTransportTerminator_ThrowsAndKeepsTheCache()
        {
            // A reply cut short by the completion window loses the device marker
            // too, so it reads as Unterminated -- which is legitimate on
            // pre-#796 firmware and cannot be treated as an error on its own.
            // The transport terminator is what separates "the firmware sent no
            // marker" from "we stopped listening": without it the exchange is
            // not known to have finished, so nothing it contains can be cached.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/remaining.bin 12" };
            // The fake appends the terminator whenever the exchange asked for it,
            // which is what a healthy device does. This knob withholds it, which
            // is what a reply cut short by the completion window looks like.
            // int.MaxValue, not 1: one stall is RETRIED now, so withholding the
            // terminator once would prove the retry works, not the throw.
            device.UnterminatedAttempts = int.MaxValue;
            device.Connect();

            await Assert.ThrowsAsync<SdCardListIncompleteException>(
                () => device.DeleteSdCardFileAsync("data.bin"));

            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WithOneUnterminatedListing_RetriesAndCaches()
        {
            // One stalled relist is a transient, and the read path has always
            // retried it. The retry re-LISTs only -- re-sending the DELETE
            // would ask the device to remove a file that is already gone.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/remaining.bin 12" };
            device.UnterminatedAttempts = 1;
            device.Connect();

            await device.DeleteSdCardFileAsync("data.bin");

            Assert.Equal("remaining.bin", Assert.Single(device.SdCardFiles).FileName);
            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(
                1,
                sent.Count(c => c.StartsWith("SYSTem:STORage:SD:DELete", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WithPersistentDeviceError_ThrowsAndKeepsTheCache()
        {
            // A delete the device refuses on every attempt: the retry above
            // exhausts without throwing, and the reply that reaches the cache
            // logic carries an error line, a valid transport terminator and no
            // device marker. Nothing in the status guards fires, ParseFileList
            // skips the error line, and the cache would become an EMPTY list --
            // telling the caller the delete worked and the card is empty, in
            // the one case where the device said neither.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
            };
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.DeleteSdCardFileAsync("locked.bin"));
            // The device's own words, not a generic "listing incomplete".
            Assert.Contains("-200", ex.Message);

            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_RefusedWithOtherFilesPresent_Throws()
        {
            // The realistic shape of a refused delete: the card still holds
            // other files, so the reply carries an error line AND real entries.
            // ThrowIfSdCardListError returns silently on that (its content
            // escape is right for the read path), so the delete's own error is
            // what has to be judged -- otherwise the caller is told the delete
            // worked while the file is still listed.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
                "Daqifi/fileA.csv 100",
                "Daqifi/fileB.csv 200",
                "__END_OF_LIST__ OK",
            };
            device.Connect();

            await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.DeleteSdCardFileAsync("fileA.csv"));

            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_RefusedAndUnterminated_ReportsTheDeviceError()
        {
            // Both failures at once: the device refuses the delete AND the
            // confirmation exchange never terminates. The completeness throw
            // used to fire first, so the caller was told "the listing did not
            // complete -- check the connection and retry" for a delete the
            // device had explicitly refused, with LastScpiError null. The
            // delete's own outcome is judged first now, so the reported error
            // is the one the device actually gave.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
            };
            device.UnterminatedAttempts = int.MaxValue;
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.DeleteSdCardFileAsync("locked.bin"));

            Assert.IsNotType<SdCardListIncompleteException>(ex);
            Assert.Contains("-200", ex.Message);
            Assert.Empty(device.SdCardFiles);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_ErrorButFileGone_TreatsTheDeleteAsDone()
        {
            // The error line carries no origin: DELETE, LIST and SYST:ERRor?
            // travel together, and the LIST has failure modes of its own. Here
            // the file the caller asked to delete is ABSENT from a listing that
            // rendered completely -- so it was deleted, whatever the error was
            // about. Reporting failure would send the caller to delete a file
            // that is already gone.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
                "Daqifi/remaining.bin 12",
                "__END_OF_LIST__ OK",
            };
            device.Connect();

            await device.DeleteSdCardFileAsync("data.bin");

            Assert.Equal("remaining.bin", Assert.Single(device.SdCardFiles).FileName);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_ErrorAndFileAbsentFromUnterminatedListing_Throws()
        {
            // Absence is only evidence if the listing is whole. Here the reply
            // is transport-terminated -- so the exchange finished -- but the
            // device sent no end-of-listing marker, which is exactly the case
            // where a walk cut short and a walk that reached the end look
            // identical. The target is missing, and that means nothing.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
                "Daqifi/remaining.bin 12",
            };
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.DeleteSdCardFileAsync("data.bin"));

            Assert.Contains("-200", ex.Message);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_ErrorAndFileAbsentFromIncompleteListing_Throws()
        {
            // The device said outright that it did not finish the walk, so the
            // file it never reached cannot be reported as deleted.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "**ERROR: -200,\"Execution error\"",
                "Daqifi/remaining.bin 12",
                "__END_OF_LIST__ INCOMPLETE",
            };
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.DeleteSdCardFileAsync("data.bin"));

            Assert.Contains("-200", ex.Message);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WithCompleteListing_UpdatesTheCache()
        {
            // The same path with an OK marker still refreshes, and the marker
            // itself is not mistaken for a file.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/remaining.bin 12",
                "__END_OF_LIST__ OK",
            };
            device.Connect();

            await device.DeleteSdCardFileAsync("data.bin");

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
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.DeleteSdCardFileAsync("data.bin"));
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_WhenLogging_ThrowsSdCardBusyException()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            // Act & Assert
            await Assert.ThrowsAsync<SdCardBusyException>(
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

        [Theory]
        [InlineData(4)]   // SYS_FS_ERROR_NO_FILE
        [InlineData(5)]   // SYS_FS_ERROR_NO_PATH
        public async Task GetSdCardFilesAsync_WhenTheDirectoryIsNotOnTheCard_ThrowsDirectoryNotFound(int fsError)
        {
            // Firmware #798: a listing no longer creates the directory it was asked to
            // read, so "that directory is not there" became observable. It is a normal
            // state -- a freshly formatted card has no log directory until the first
            // capture writes one -- and a caller that renders it like a corrupt
            // filesystem shows a scary message for "you have not logged anything yet".
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                $"[Error:{fsError}]Failed to open directory [DAQiFi]",
                "__END_OF_LIST__ FAILED",
            };
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardDirectoryNotFoundException>(
                () => device.GetSdCardFilesAsync());

            Assert.Equal("DAQiFi", ex.DirectoryPath);
            // Still an SdCardFilesystemException, so every existing catch site and the
            // desktop classifier keep working unchanged.
            Assert.IsAssignableFrom<SdCardFilesystemException>(ex);
        }

        [Theory]
        [InlineData(1)]   // SYS_FS_ERROR_DISK_ERR
        [InlineData(3)]   // SYS_FS_ERROR_NOT_READY
        [InlineData(13)]  // SYS_FS_ERROR_NO_FILESYSTEM territory -- anything unfamiliar
        public async Task GetSdCardFilesAsync_WhenTheDirectoryFailsForAnotherReason_StaysAFilesystemError(int fsError)
        {
            // The other side of the same predicate. A disk error, an unready card or a
            // code this client has never seen must NOT be narrowed to "not found" --
            // that would tell a user with a dying card that they simply have no files.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string>
            {
                $"[Error:{fsError}]Failed to open directory [DAQiFi]",
            };
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardFilesystemException>(
                () => device.GetSdCardFilesAsync());

            Assert.IsNotType<SdCardDirectoryNotFoundException>(ex);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenTheNotFoundLineHasNoBracketedPath_StillThrowsWithANullPath()
        {
            // The pre-#798 firmware phrasing has no bracketed path. The code still
            // classifies, and DirectoryPath is honestly null rather than a fragment of
            // the message.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var response = new List<string> { "[Error:5]Failed to open directory /Daqifi" };
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.ResponseSequence.Enqueue(new List<string>(response));
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardDirectoryNotFoundException>(
                () => device.GetSdCardFilesAsync());

            Assert.Null(ex.DirectoryPath);
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
            // Arrange - device returns no listing lines (empty directory, no errors) but does
            // answer the terminator query. This is the legitimate "0 files" case and must keep
            // its existing behavior: an empty list, no exception (#396).
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string>());
            device.Connect();

            // Act
            var files = await device.GetSdCardFilesAsync();

            // Assert
            Assert.Empty(files);
            Assert.Equal(1, device.ExecuteTextCommandCallCount);
        }

        #region Timed-out vs. empty listing (issue #396)

        [Fact]
        public async Task GetSdCardFilesAsync_SendsTerminatorQueryAfterListCommand()
        {
            // The terminator only proves the listing is complete if it is requested AFTER the
            // listing itself, in the same exchange — the transport is ordered, so its reply
            // cannot overtake listing lines the device had already written.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log_20240115_103000.bin" };
            device.Connect();

            await device.GetSdCardFilesAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var listIndex = sentCommands.IndexOf("SYSTem:STORage:SD:LIST?");
            var terminatorIndex = sentCommands.IndexOf("SYSTem:ERRor?");
            Assert.True(listIndex >= 0, "The file-list query was not sent.");
            Assert.True(terminatorIndex > listIndex, "The terminator query must be sent after the file-list query.");
        }

        [Fact]
        public async Task GetSdCardFilesAsync_TerminatorReplyIsNotParsedAsAFile()
        {
            // The terminator is a protocol artifact, not directory content: it must never reach
            // the file parser and show up as a phantom SD card file.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log_20240115_103000.bin" };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Single(files);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenDeviceNeverAnswers_ThrowsInsteadOfReturningEmptyList()
        {
            // The bug in #396: a device that never answered produced the exact same empty list as
            // a healthy empty card, so downstream rendered "SD card OK - 0 files" for an
            // unreachable device holding data. Both attempts go unanswered here.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log_20240115_103000.bin" };
            device.Connect();

            // A first, healthy listing populates the cache...
            Assert.Single(await device.GetSdCardFilesAsync());

            // ...then the device goes silent.
            device.CannedTextResponse = new List<string>();
            device.UnterminatedAttempts = int.MaxValue;

            await Assert.ThrowsAsync<SdCardListIncompleteException>(
                () => device.GetSdCardFilesAsync());

            // The cache must not be overwritten with a listing we never actually received.
            Assert.Single(device.SdCardFiles);
            Assert.Equal("log_20240115_103000.bin", device.SdCardFiles[0].FileName);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenListingIsTruncated_ThrowsRatherThanReturningAShortList()
        {
            // The case no downstream mitigation can reach: the device answered, but stopped
            // part-way through the listing. Corroborating with a later query (as the Avalonia port
            // does) cannot detect this — only the missing terminator can.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "Daqifi/log_20240115_103000.bin",
                "Daqifi/log_20240115_1030",
            };
            device.UnterminatedAttempts = int.MaxValue;
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardListIncompleteException>(
                () => device.GetSdCardFilesAsync());

            // The partial response is preserved for diagnostics rather than silently returned.
            Assert.Equal(2, ex.RawDeviceResponse.Count);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WithStaleTerminatorAheadOfTheListing_StillReturnsTheFiles()
        {
            // A terminator reply from a previous, timed-out exchange can still be in the transport
            // buffer and lead this response. Splitting at the FIRST match would discard the real
            // listing behind it and report an empty card — the very failure #396 is about.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>
            {
                "-200,\"Execution error\"",   // stale reply left over from an earlier exchange
                "Daqifi/log_20240115_103000.bin",
                "Daqifi/data.bin",
            };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            // Both files survive, and the stale reply is not parsed as a phantom file.
            Assert.Equal(2, files.Count);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
            Assert.Equal("data.bin", files[1].FileName);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenStaleMarkerAccompaniesThisRequestsError_StillThrows()
        {
            // A stale end-of-listing marker, left in the buffer by an earlier timed-out
            // exchange, arrives ahead of THIS request's own SCPI error. The marker is
            // framing and must not vouch for the reply: if it counted as content, the
            // error would be skipped, the status would read Unterminated rather than
            // Failed, and the caller would be handed a confidently empty card for a
            // listing the device never produced.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            var staleMarkerThenError = new List<string>
            {
                "__END_OF_LIST__ OK",
                "**ERROR: -200,\"Execution error\"",
            };

            // Both the first attempt and its retry see it, so the loop runs out of
            // attempts holding the error rather than recovering on the second.
            device.ResponseSequence.Enqueue(new List<string>(staleMarkerThenError));
            device.ResponseSequence.Enqueue(new List<string>(staleMarkerThenError));
            device.Connect();

            var ex = await Assert.ThrowsAsync<SdCardOperationException>(
                () => device.GetSdCardFilesAsync());

            Assert.Contains("Execution error", ex.Message);
            Assert.Equal("**ERROR: -200,\"Execution error\"", ex.LastScpiError);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenMarkerIsTheOnlyLine_ReportsAnEmptyCard()
        {
            // The other side of the same predicate: a genuinely empty directory ends
            // with nothing but the marker. Treating the marker as framing must not
            // turn that into an error -- there is no SCPI error to surface, so the
            // empty listing is the right answer.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "__END_OF_LIST__ OK" };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Empty(files);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenTerminatorMissingOnFirstAttemptOnly_RetriesAndSucceeds()
        {
            // A one-off stall is retried on the same terms as a transient SCPI error, so a single
            // dropped reply does not become a user-visible failure.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string>());
            device.ResponseSequence.Enqueue(new List<string> { "Daqifi/log_20240115_103000.bin" });
            device.UnterminatedAttempts = 1;
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Single(files);
            Assert.Equal("log_20240115_103000.bin", files[0].FileName);
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_WhenDeviceNeverAnswers_RestoresLanInterface()
        {
            // The throw must not skip the interface restore — the SD subsystem would be left
            // enabled and the LAN disabled for every later command.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string>();
            device.UnterminatedAttempts = int.MaxValue;
            device.Connect();

            await Assert.ThrowsAsync<SdCardListIncompleteException>(
                () => device.GetSdCardFilesAsync());

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands); // DisableStorageSd
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan
        }

        #endregion

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
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.FormatSdCardAsync());
        }

        [Fact]
        public async Task FormatSdCardAsync_WhenLogging_ThrowsSdCardBusyException()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            // Act & Assert
            await Assert.ThrowsAsync<SdCardBusyException>(
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

            await Assert.ThrowsAsync<DeviceNotConnectedException>(
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
        public async Task GetSdCardStorageAsync_WithUndefinedHeaderError_ThrowsFeatureNotSupportedException()
        {
            // -113 "Undefined header" means the firmware doesn't recognize the storage query at
            // all (e.g. it predates the version that introduced it) — a distinct, typed failure
            // from a generic SdCardOperationException.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.4.3";
            device.Metadata.DeviceType = DeviceType.Nyquist1;
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -113, \"Undefined header\"" });
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -113, \"Undefined header\"" });
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.GetSdCardStorageAsync());

            Assert.Equal(2, device.ExecuteTextCommandCallCount);
            Assert.Equal(DeviceFeature.SdStorageQuery, ex.Feature);
            Assert.Equal(DaqifiStreamingDevice.MinSupportedFirmware, ex.RequiredVersion);
            Assert.Equal("3.4.3", ex.ActualVersion);
            Assert.Equal(DeviceType.Nyquist1, ex.Board);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WithSpaceDelimitedUndefinedHeaderError_ThrowsFeatureNotSupportedException()
        {
            // The shared ScpiResponseClassifier treats ':', space, and tab as equally valid
            // delimiters after the ERROR token, so the -113 code parser must recognize a
            // space-delimited line too, not just the colon-delimited form.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR -113, \"Undefined header\"" });
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR -113, \"Undefined header\"" });
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.GetSdCardStorageAsync());

            Assert.Equal(DeviceFeature.SdStorageQuery, ex.Feature);
        }

        [Fact]
        public async Task GetSdCardStorageAsync_WithUndefinedHeaderError_AndUnknownDeviceType_LeavesBoardNull()
        {
            // Metadata.DeviceType defaults to Unknown until a part number has been reported;
            // that sentinel should not be forwarded as a "known" board.
            var device = new RetryableSdCardStreamingDevice("TestDevice");
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -113, \"Undefined header\"" });
            device.ResponseSequence.Enqueue(new List<string> { "**ERROR: -113, \"Undefined header\"" });
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.GetSdCardStorageAsync());

            Assert.Null(ex.Board);
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
        public async Task GetSdCardStorageAsync_WhenLogging_ThrowsSdCardBusyException()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");

            await Assert.ThrowsAsync<SdCardBusyException>(
                () => device.GetSdCardStorageAsync());
        }

        #endregion

        #region CheckSdCardSpaceAsync Tests

        [Fact]
        public async Task CheckSdCardSpaceAsync_WhenNearlyFull_RaisesWarningAndReturnsResult()
        {
            // 50 MB free of a 4 GB card — below the 100 MB default floor.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "52428800,4294967296" };
            device.Connect();

            LowSdSpaceWarningEventArgs? raised = null;
            device.LowSdSpaceWarning += (_, e) => raised = e;

            var result = await device.CheckSdCardSpaceAsync();

            Assert.True(result.ShouldWarn);
            Assert.True(result.IsNearlyFull);
            Assert.NotNull(raised);
            Assert.Same(result, raised!.Result);
        }

        [Fact]
        public async Task CheckSdCardSpaceAsync_WarningSenderIsTheDevice()
        {
            // The space check now lives in a collaborator, but the event belongs to the device's
            // public surface: subscribers key off the sender to tell devices apart. Nothing
            // asserted this before the split — every existing subscriber discards the sender —
            // so a collaborator raising the event in its own name would have been a silent,
            // compile-clean behavior change (#344).
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "52428800,4294967296" };
            device.Connect();

            object? sender = null;
            device.LowSdSpaceWarning += (s, _) => sender = s;

            await device.CheckSdCardSpaceAsync();

            Assert.Same(device, sender);
        }

        [Fact]
        public async Task CheckSdCardSpaceAsync_WhenPlentyOfSpace_DoesNotRaiseWarning()
        {
            // ~3.7 GB free of a 4 GB card.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "4000000000,4294967296" };
            device.Connect();

            var raisedCount = 0;
            device.LowSdSpaceWarning += (_, _) => raisedCount++;

            var result = await device.CheckSdCardSpaceAsync();

            Assert.False(result.ShouldWarn);
            Assert.Equal(0, raisedCount);
        }

        [Fact]
        public async Task CheckSdCardSpaceAsync_WithEstimateThatWontFit_RaisesWarning()
        {
            // 200 MB free; an 8 h capture at 8000 B/s (~220 MB) won't fit.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "209715200,4294967296" };
            device.Connect();

            LowSdSpaceWarningEventArgs? raised = null;
            device.LowSdSpaceWarning += (_, e) => raised = e;

            var estimate = new SdCardCaptureEstimate(1000, 4, TimeSpan.FromHours(8), bytesPerSamplePerChannel: 2);
            var result = await device.CheckSdCardSpaceAsync(estimate);

            Assert.True(result.IsInsufficientForCapture);
            Assert.False(result.IsNearlyFull);
            Assert.NotNull(raised);
            Assert.NotNull(result.EstimatedTimeUntilFull);
        }

        [Fact]
        public async Task CheckSdCardSpaceAsync_WhenDisconnected_Throws()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");

            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.CheckSdCardSpaceAsync());
        }

        [Fact]
        public async Task CheckSdCardSpaceAsync_QueriesSdSpace()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "52428800,4294967296" };
            device.Connect();

            await device.CheckSdCardSpaceAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:SPACe?", sentCommands);
        }

        #endregion

        #region SetSdCardMinimumFreeSpace Tests

        [Fact]
        public void SetSdCardMinimumFreeSpace_WhenConnected_SendsCommand()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            device.SetSdCardMinimumFreeSpace(52428800);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:MINFree 52428800", sentCommands);
        }

        [Fact]
        public void SetSdCardMinimumFreeSpace_WhenDisconnected_Throws()
        {
            var device = new DaqifiStreamingDevice("TestDevice");

            Assert.Throws<DeviceNotConnectedException>(() => device.SetSdCardMinimumFreeSpace(52428800));
        }

        [Fact]
        public void SetSdCardMinimumFreeSpace_WithNegativeValue_Throws()
        {
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetSdCardMinimumFreeSpace(-1));
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
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.DownloadSdCardFileAsync("test.bin", stream));
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_OverWifi_BelowMinFirmware_ThrowsFeatureNotSupported()
        {
            // Over WiFi (non-USB), SD file transfer requires firmware >= v3.7.0 (#598/#599).
            // A below-minimum reported version gets the typed, actionable feature exception —
            // superseding the old blanket "only supported over USB" InvalidOperationException.
            var device = new TestableNonUsbStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.6.3";
            device.Connect();
            using var stream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.DownloadSdCardFileAsync("test.bin", stream));
            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
            Assert.Equal(new FirmwareVersion(3, 7, 0, null, 0), ex.RequiredVersion);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_OverWifi_UnparseableFirmware_ThrowsFeatureNotSupported()
        {
            // An unset / unparseable reported version is treated as unsupported over WiFi.
            var device = new TestableNonUsbStreamingDevice("TestDevice");
            device.Connect();
            using var stream = new MemoryStream();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.DownloadSdCardFileAsync("test.bin", stream));
            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
        }

        #region SD-over-WiFi firmware gate (#598/#599 — requires firmware >= v3.7.0)

        [Theory]
        [InlineData("3.7.0")]
        [InlineData("3.7.2")]
        [InlineData("3.8.0")]
        public async Task GetSdCardFilesAsync_OverWifi_AtOrAboveMinFirmware_Succeeds(string firmware)
        {
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmware;
            device.CannedTextResponse = new List<string> { "Daqifi/log.bin" };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Single(files);
            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:LIST?", sent);
            // Over WiFi the LAN interface must NOT be toggled — disabling it would drop the TCP
            // channel carrying the SD reply (#598/#599: the SPI driver arbitrates instead).
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 0", sent); // DisableNetworkLan
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // EnableNetworkLan
            // The SD subsystem is still toggled (that does not touch the LAN).
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sent);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_OverUsb_TogglesLanInterface()
        {
            // Regression: over USB the LAN interface IS disabled (free the shared SPI bus) and
            // restored — the transport-aware PrepareSdInterface/PrepareLanInterface must keep this.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log.bin" };
            device.Connect();

            await device.GetSdCardFilesAsync();

            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sent); // DisableNetworkLan
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // EnableNetworkLan (restore)
        }

        [Fact]
        public async Task SdCardTextOperations_HandTheLanRestoreToTheExchangeAsItsFinalizePhase()
        {
            // #407: the restore has to travel through the exchange's finalize phase, because that
            // is what puts it under the same lock acquisition as the matching switch. This device
            // drops the finalize phase on the floor — so if any restore still reaches the wire, it
            // came from a caller-side finally running after the lock was already released, which is
            // the defect. Every SD text operation is checked, since they share the pairing.
            var device = new FinalizeDroppingSdCardDevice("TestDevice");
            device.CannedTextResponse = new List<string> { "Daqifi/log.bin" };
            device.Connect();

            await device.GetSdCardFilesAsync();
            await device.DeleteSdCardFileAsync("log.bin");

            device.CannedTextResponse = new List<string> { "1024,4096" };
            await device.GetSdCardStorageAsync();

            var sent = device.SentMessages.Select(m => m.Data).ToList();

            // The switch still happened — this is not a device that simply sent nothing.
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 0", sent); // DisableNetworkLan
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sent);       // EnableStorageSd

            // And the restore did not, because the only route to it was the phase this device
            // discarded.
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // EnableNetworkLan
            Assert.DoesNotContain("SYSTem:STORage:SD:ENAble 0", sent);       // DisableStorageSd

            // Three operations, each of which offered the exchange a finalize phase (the listing
            // and the delete send one exchange apiece here; storage the same).
            Assert.Equal(3, device.ExchangesOfferedAFinalizePhase);
            Assert.Equal(0, device.ExchangesWithoutAFinalizePhase);
        }

        [Theory]
        [InlineData("3.6.3")]
        [InlineData("3.5.0")]
        [InlineData("")]
        [InlineData("not-a-version")]
        [InlineData("999999999999999999.0.0")] // overflows Int32 — must fail closed, not crash
        public async Task GetSdCardFilesAsync_OverWifi_BelowMinOrUnparseableFirmware_ThrowsFeatureNotSupported(string firmware)
        {
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmware;
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.GetSdCardFilesAsync());
            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
            Assert.Equal(new FirmwareVersion(3, 7, 0, null, 0), ex.RequiredVersion);
            // The gate short-circuits up front — no SD command should have been dispatched over
            // a transport the firmware can't service (else it would stall on the shared SPI bus).
            Assert.DoesNotContain("SYSTem:STORage:SD:LIST?", device.SentMessages.Select(m => m.Data));
        }

        [Theory]
        [InlineData("3.4.3")]
        [InlineData("3.0.0b0")]
        public async Task GetSdCardFilesAsync_OverUsb_IsNotFirmwareGated(string oldFirmware)
        {
            // Over USB the SD file ops are available on all SD-capable firmware — the WiFi gate
            // must NOT apply, even for firmware far below v3.7.0.
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = oldFirmware;
            device.CannedTextResponse = new List<string> { "Daqifi/log.bin" };
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Single(files);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_OverWifi_AtMinFirmware_Succeeds()
        {
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.7.0";
            device.CannedFileData = Encoding.ASCII.GetBytes("hello");
            device.Connect();
            using var stream = new MemoryStream();

            await device.DownloadSdCardFileAsync("data.bin", stream);

            Assert.Equal("hello", Encoding.ASCII.GetString(stream.ToArray()));
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_OverWifi_BelowMinFirmware_ThrowsFeatureNotSupported()
        {
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.6.3";
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.DeleteSdCardFileAsync("data.bin"));
            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
        }

        [Fact]
        public async Task DeleteSdCardFileAsync_OverWifi_AtMinFirmware_Succeeds()
        {
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.7.2";
            device.Connect();

            // Passes the gate and completes (no FeatureNotSupportedException).
            await device.DeleteSdCardFileAsync("data.bin");

            Assert.NotEmpty(device.SentMessages);
        }

        [Theory]
        [InlineData("3.6.3")]
        [InlineData("")]
        public async Task GetSdCardStorageAsync_OverWifi_BelowMinFirmware_ThrowsFeatureNotSupported(string firmware)
        {
            // The storage-space query drives the SD card through the same transport-aware interface
            // prep, so it carries the same SD-over-WiFi firmware requirement and must be gated too.
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmware;
            device.Connect();

            var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
                () => device.GetSdCardStorageAsync());
            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
            // The gate short-circuits before any SD command touches the shared SPI bus.
            Assert.DoesNotContain("SYSTem:STORage:SD:SPACe?", device.SentMessages.Select(m => m.Data));
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_OverWifi_ChunkedAndThrottledDelivery_ReceivesTheWholeFile()
        {
            // The end-to-end shape of a WiFi download: the firmware clamps every chunk it writes to
            // the TCP buffer at 1024 bytes (#599) and the chunks arrive with gaps, so the terminator
            // lands split across reads and the file is reassembled from a long run of short ones.
            var fileData = new byte[5_000];
            new Random(599).NextBytes(fileData);

            var device = new ChunkedWifiDownloadDevice("TestDevice", fileData, chunkSize: 1024);
            device.Metadata.FirmwareVersion = "3.7.2";
            device.Connect();
            using var destination = new MemoryStream();

            var result = await device.DownloadSdCardFileAsync("wifi.bin", destination);

            Assert.Equal(fileData.Length, result.FileSize);
            Assert.Equal(fileData, destination.ToArray());
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_OverWifi_DeviceGoesSilent_StallsOnTheIdleWindow()
        {
            // A socket never surfaces silence of its own accord, so before the inactivity window
            // this sat on the read until the 30-minute download budget expired. It has to give up
            // in the window instead — and say the device stopped feeding it, not that time ran out.
            var device = new SilentWifiDownloadDevice("TestDevice")
            {
                IdleTimeoutOverride = TimeSpan.FromMilliseconds(200)
            };
            device.Metadata.FirmwareVersion = "3.7.2";
            device.Connect();
            using var destination = new MemoryStream();

            var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
                () => device.DownloadSdCardFileAsync("wifi.bin", destination));

            Assert.Equal(SdCardTransferStallReason.NoDataReceived, ex.Reason);
            Assert.Equal(TimeSpan.FromMilliseconds(200), ex.Timeout);
        }

        [Fact]
        public async Task StopSdCardLoggingAsync_OverWifi_DoesNotReEnableLan()
        {
            // LAN:ENAbled 1 re-initializes the WiFi module, which would drop the very connection
            // this command arrived on. Nothing disabled the LAN over WiFi in the first place, so
            // there is nothing to restore — the mirror of PrepareLanInterface's transport check.
            var device = new TestableNonUsbSdCardStreamingDevice("TestDevice");
            device.Connect();

            await device.StopSdCardLoggingAsync();

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(new[] { "SYSTem:StopStreamData", "SYSTem:STORage:SD:ENAble 0" }, sentCommands);
        }

        #endregion

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
        public async Task DownloadSdCardFileAsync_WhenLogging_ThrowsSdCardBusyException()
        {
            // Arrange
            var device = new TestableDownloadDevice("TestDevice");
            device.Connect();
            await device.StartSdCardLoggingAsync("test.bin");
            using var stream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<SdCardBusyException>(
                () => device.DownloadSdCardFileAsync("data.bin", stream));
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

        [Fact]
        public async Task DownloadSdCardFileAsync_MarkerOnlyTransferOnEveryAttempt_ThrowsSdCardEmptyTransferException()
        {
            // Arrange — the device's SD subsystem stays wedged across every GET retry (#264).
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>());
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
                () => device.DownloadSdCardFileAsync("data.bin", destinationStream));
            Assert.Equal("data.bin", ex.FileName);
            Assert.Empty(destinationStream.ToArray());

            // Two GET attempts: the initial send plus one retry.
            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(2, getCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_MarkerOnlyThenSuccess_RetriesAndSucceeds()
        {
            // Arrange — the device's first GET wedges (marker-only), the retry succeeds.
            var fileData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), fileData);
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act
            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert
            Assert.Equal(fileData, destinationStream.ToArray());
            Assert.Equal(fileData.Length, result.FileSize);

            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(2, getCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_ListedZeroByteFile_ReturnsEmptyFileWithoutRetrying()
        {
            // Arrange — the listing reports the file as genuinely 0 bytes (an interrupted logging
            // session leaves these on a FAT card). A marker-only transfer is then the correct,
            // complete answer, not a wedged SD subsystem, so it must neither retry nor throw
            // "power-cycle the device" at the user (#398 gap 2).
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>());
            device.ListingLines.Add("Daqifi/data.bin 0");
            device.Connect();
            await device.GetSdCardFilesAsync();
            device.SentMessages.Clear();

            using var destinationStream = new MemoryStream();

            // Act
            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert
            Assert.Equal(0, result.FileSize);
            Assert.Empty(destinationStream.ToArray());

            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(1, getCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_ListedNonEmptyFile_RetriesThenReportsListedSize()
        {
            // Arrange — the listing says 4096 bytes, so a marker-only transfer really is a wedged
            // SD subsystem: the #264 retry still applies and the failure now carries the evidence.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>());
            device.ListingLines.Add("Daqifi/data.bin 4096");
            device.Connect();
            await device.GetSdCardFilesAsync();
            device.SentMessages.Clear();

            using var destinationStream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
                () => device.DownloadSdCardFileAsync("data.bin", destinationStream));
            Assert.Equal(4096, ex.ListedSizeInBytes);

            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(2, getCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_ShortTransferForListedFile_ThrowsInsteadOfReportingSuccess()
        {
            // Arrange — the listing says 6159 bytes and the device answers the GET with a SCPI
            // error line followed by the end-of-file marker, which is what the bench Nq1 actually
            // does for a file its SD buffer can no longer serve. That used to come back as a
            // successful 34-byte download with the error text standing in for the log (#539).
            var errorLine = Encoding.ASCII.GetBytes("**ERROR: -200, \"Execution error\"\r\n");
            var device = new TestableRetryDownloadDevice(errorLine, errorLine);
            device.ListingLines.Add("Daqifi/data.bin 6159");
            device.Connect();
            await device.GetSdCardFilesAsync();
            device.SentMessages.Clear();

            using var destinationStream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardTruncatedTransferException>(
                () => device.DownloadSdCardFileAsync("data.bin", destinationStream));
            Assert.Equal("data.bin", ex.FileName);
            Assert.Equal(6159, ex.ListedSizeInBytes);
            Assert.Equal(errorLine.Length, ex.BytesReceived);

            // A single GET: unlike the marker-only case this is NOT retried, because the partial
            // bytes are already in the caller's stream and a second attempt would append to them.
            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(1, getCommands);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_TransferMatchingListedSize_Succeeds()
        {
            // The companion to the case above: a device that serves the whole file still reports a
            // plain success, so the guard cannot be rejecting good downloads.
            var fileData = new byte[512];
            new Random(539).NextBytes(fileData);
            var device = new TestableRetryDownloadDevice(fileData);
            device.ListingLines.Add("Daqifi/data.bin 512");
            device.Connect();
            await device.GetSdCardFilesAsync();

            using var destinationStream = new MemoryStream();

            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            Assert.Equal(512, result.FileSize);
            Assert.Equal(fileData, destinationStream.ToArray());
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_AmbiguousListedName_FallsBackToConservativeBehavior()
        {
            // Arrange — the listing keeps only leaf names, so the same name can appear twice from
            // different directories with different sizes. Trusting the first 0 would wave through
            // exactly the wedged-subsystem failure the empty-transfer guard exists to catch, so an
            // ambiguous name is treated as "size unknown".
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>());
            device.ListingLines.Add("Daqifi/data.bin 0");
            device.ListingLines.Add("Daqifi/archive/data.bin 4096");
            device.Connect();
            await device.GetSdCardFilesAsync();

            using var destinationStream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
                () => device.DownloadSdCardFileAsync("data.bin", destinationStream));
            Assert.Null(ex.ListedSizeInBytes);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WithoutAPriorListing_FallsBackToConservativeBehavior()
        {
            // Arrange — nothing has been listed, so there is no size to consult and the #264
            // retry-then-throw behavior must stand unchanged.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>());
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
                () => device.DownloadSdCardFileAsync("data.bin", destinationStream));
            Assert.Null(ex.ListedSizeInBytes);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_ListTerminator_IsNotParsedAsAFileEntry()
        {
            // The #396 end-of-listing terminator shares the response with the listing. It is
            // stripped before parsing, and that stripping is load-bearing for gap 2: the reply
            // "0,\"No error\"" splits on its first space into a path and a size token, so if it
            // ever reached SdCardFileListParser it would become a phantom 0-byte file — which
            // would then be handed to the receiver as a legitimate empty download.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>());
            device.ListingLines.Add("Daqifi/log_20240115_103000.bin 4096");
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            var file = Assert.Single(files);
            Assert.Equal("log_20240115_103000.bin", file.FileName);
            Assert.Equal(4096, file.SizeInBytes);
            Assert.DoesNotContain(files, f => f.FileName.Contains("No error", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, f => f.FileName.StartsWith("0,", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetSdCardFilesAsync_UnterminatedFirstAttempt_RetriesThenKeepsSizesIntact()
        {
            // #396 owns a retry-on-unterminated loop around the LIST. Confirm it recovers with the
            // size plumbing intact rather than surfacing an incomplete listing, so gap 2 still has
            // a real size to pass to the receiver after a retried listing.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>())
            {
                UnterminatedAttempts = 1
            };
            device.ListingLines.Add("Daqifi/data.bin 0");
            device.ListingLines.Add("Daqifi/other.bin 2048");
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Equal(2, files.Count);
            Assert.Equal(0, files[0].SizeInBytes);
            Assert.Equal(2048, files[1].SizeInBytes);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_AfterRetriedListing_StillDownloadsZeroByteFileWithoutRetrying()
        {
            // The two retry loops are on different operations (#396's is around the LIST exchange,
            // the GET retry is around the transfer) and must not compound: a listing that needed a
            // retry still yields a listed size of 0, and the download then completes on its FIRST
            // GET rather than inheriting a retry from the listing.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>(), Array.Empty<byte>())
            {
                UnterminatedAttempts = 1
            };
            device.ListingLines.Add("Daqifi/data.bin 0");
            device.Connect();
            await device.GetSdCardFilesAsync();
            device.SentMessages.Clear();

            using var destinationStream = new MemoryStream();

            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            Assert.Equal(0, result.FileSize);
            var getCommands = device.SentMessages.Select(m => m.Data).Count(c => c.Contains("SD:GET"));
            Assert.Equal(1, getCommands);
        }

        [Fact]
        public async Task GetSdCardFilesAsync_ListingWithSizes_ExposesReportedSizes()
        {
            // Firmware emits "<path> <size>" per entry; the size is what a later download needs to
            // tell a legitimately empty file from a wedged subsystem, so it must survive parsing.
            var device = new TestableRetryDownloadDevice(Array.Empty<byte>());
            device.ListingLines.Add("Daqifi/log_20240115_103000.bin 4096");
            device.ListingLines.Add("Daqifi/empty.bin 0");
            device.ListingLines.Add("Daqifi/nosize.bin");
            device.Connect();

            var files = await device.GetSdCardFilesAsync();

            Assert.Equal(3, files.Count);
            Assert.Equal(4096, files[0].SizeInBytes);
            Assert.Equal(0, files[1].SizeInBytes);
            Assert.Null(files[2].SizeInBytes);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenTransferParksIgnoringItsToken_ThrowsTimeoutException()
        {
            // #399: against a wedged SD subsystem the transfer parks somewhere that never looks
            // at the token it was handed, so the only thing that can end the call is a deadline
            // the download enforces itself.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var destinationStream = new MemoryStream();

            try
            {
                var elapsed = Stopwatch.StartNew();
                var opTask = device.DownloadSdCardFileAsync("data.bin", destinationStream);

                // Guard the assertion rather than the test run: if the deadline never fires this
                // fails instead of hanging CI forever.
                var winner = await Task.WhenAny(opTask, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.Same((Task)opTask, winner);

                var ex = await Assert.ThrowsAsync<TimeoutException>(() => opTask);
                elapsed.Stop();

                Assert.Contains("data.bin", ex.Message);

                // It waited for the budget instead of failing early — the deadline is what ended
                // it, not some unrelated fault.
                Assert.True(
                    elapsed.Elapsed >= TimeSpan.FromMilliseconds(300),
                    $"Download failed after only {elapsed.ElapsedMilliseconds}ms, before the budget elapsed.");
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenTransferParksSynchronously_StillTimesOut()
        {
            // The suspected park in #399 (a blocking SerialPort.Write on a device that stopped
            // draining) is in the transfer's SYNCHRONOUS prefix — before the first await. Unless
            // that prefix runs on the worker task, it blocks the caller before any deadline can
            // even be armed, and the call never returns a Task at all. Hence the Task.Run: it
            // keeps the test thread free so a regression fails here instead of wedging the run.
            var device = new ParkedDownloadDevice(ParkMode.Synchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var destinationStream = new MemoryStream();

            try
            {
                var opTask = Task.Run(() => device.DownloadSdCardFileAsync("data.bin", destinationStream));

                var winner = await Task.WhenAny(opTask, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.Same((Task)opTask, winner);

                await Assert.ThrowsAsync<TimeoutException>(() => opTask);
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenTheTransferIsAbandoned_DoesNotRestoreTheLanInterface()
        {
            // #407 / #399: an abandoned transfer is still running and still owns the transport —
            // that is why the download gate stays held until it unwinds. Sending the LAN restore
            // now would put commands onto a link that transfer is still reading from, on a device
            // that has already stopped answering. The caller is told to reconnect or power-cycle,
            // and both re-establish the interface anyway.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var destinationStream = new MemoryStream();

            try
            {
                var opTask = device.DownloadSdCardFileAsync("data.bin", destinationStream);

                var winner = await Task.WhenAny(opTask, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.Same((Task)opTask, winner);
                await Assert.ThrowsAsync<TimeoutException>(() => opTask);

                var sent = device.SentCommandsSnapshot();
                Assert.Contains("SYSTem:StopStreamData", sent); // the pre-flight stop did happen
                Assert.DoesNotContain("SYSTem:STORage:SD:ENAble 0", sent);       // DisableStorageSd
                Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // EnableNetworkLan
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenTheTransferCompletes_StillRestoresTheLanInterface()
        {
            // The complement, so skipping the restore for an abandoned transfer cannot quietly
            // become "the download never restores the interface".
            var device = new TestableDownloadDevice("TestDevice");
            device.CannedFileData = Encoding.ASCII.GetBytes("hello sd card");
            device.Connect();
            using var destinationStream = new MemoryStream();

            await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sent);       // DisableStorageSd
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // EnableNetworkLan
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenTransferParksIgnoringItsToken_CallerCancellationStillEndsTheCall()
        {
            // The consumer-side stall watchdog described in #399: it cancels its token at 90s and
            // nothing happens, because the parked path never polls it. The download must observe
            // the caller's token itself so cancelling actually ends the await — well before the
            // (deliberately distant) deadline below.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromSeconds(60));
            device.Connect();
            using var destinationStream = new MemoryStream();
            using var cts = new CancellationTokenSource();

            try
            {
                var opTask = device.DownloadSdCardFileAsync("data.bin", destinationStream, null, cts.Token);

                // Only cancel once the transfer is genuinely parked, so the test proves the token
                // reaches a call already in flight rather than the up-front guard.
                Assert.True(device.Parked.Wait(TimeSpan.FromSeconds(30)), "Transfer never reached the park.");
                cts.Cancel();

                var winner = await Task.WhenAny(opTask, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.Same((Task)opTask, winner);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opTask);
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhileAPreviousTransferIsStillAbandoned_FailsFast()
        {
            // An abandoned transfer still owns the transport, so a retry must be refused rather
            // than start a second reader on the same stream (and stack another blocked thread).
            // A consumer looping over a wedged card's files gets one timeout, then cheap failures.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var firstDestination = new MemoryStream();
            using var secondDestination = new MemoryStream();

            try
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => device.DownloadSdCardFileAsync("data.bin", firstDestination));

                // The first transfer is still parked, so the gate is still held. The exception TYPE
                // is what proves the refusal came from the gate: waiting out another deadline would
                // have produced TimeoutException, never InvalidOperationException. The wall-clock
                // bound is only a hang guard, kept loose so a contended CI runner can't fail it.
                var secondCall = device.DownloadSdCardFileAsync("other.bin", secondDestination);
                var winner = await Task.WhenAny(secondCall, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.Same((Task)secondCall, winner);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => secondCall);
                Assert.Contains("abandoned", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_WhenGateIsHeldAndCallerCancelled_ReportsCancellation()
        {
            // Cancellation outranks the gate: a caller who cancelled should get their own
            // cancellation back, not a report about a different transfer they can do nothing about.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var firstDestination = new MemoryStream();
            using var secondDestination = new MemoryStream();

            try
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => device.DownloadSdCardFileAsync("data.bin", firstDestination));

                // Cancel from inside the pre-flight StopStreaming send: that lands in the exact
                // window this guards — after DownloadSdCardFileAsync's entry check, before the
                // gate is examined. A token cancelled before the call would just trip the entry
                // check and prove nothing.
                using var cts = new CancellationTokenSource();
                device.CancelOnNextSend = cts;

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => device.DownloadSdCardFileAsync("other.bin", secondDestination, null, cts.Token));
            }
            finally
            {
                device.Release();
            }
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_AfterAnAbandonedTransferUnwinds_IsAllowedAgain()
        {
            // The gate is a quarantine, not a one-shot latch: once the abandoned worker finally
            // returns, the transport is free and downloads are accepted again.
            var device = new ParkedDownloadDevice(ParkMode.Asynchronous, TimeSpan.FromMilliseconds(300));
            device.Connect();
            using var destination = new MemoryStream();

            await Assert.ThrowsAsync<TimeoutException>(
                () => device.DownloadSdCardFileAsync("data.bin", destination));

            // Let the abandoned worker unwind, then wait for the gate to come back.
            device.Release();

            InvalidOperationException? lastRefusal = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    // The park device completes its raw capture without ever calling the transfer
                    // body, so an accepted download reports a zero-byte result rather than throwing.
                    await device.DownloadSdCardFileAsync("data.bin", destination);
                    lastRefusal = null;
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    lastRefusal = ex;
                    await Task.Delay(25);
                }
            }

            Assert.Null(lastRefusal);
        }

        [Fact]
        public async Task DownloadSdCardFileAsync_SlowButHealthyTransfer_IsNotAborted()
        {
            // The bounding must only end transfers that are stuck. A slow-but-progressing one —
            // a large file, a busy device — has to run to completion untouched.
            var fileData = new byte[240];
            new Random(399).NextBytes(fileData);

            var device = new SlowDownloadDevice(
                fileData,
                chunkSize: 16,
                chunkDelay: TimeSpan.FromMilliseconds(40),
                budget: TimeSpan.FromSeconds(10));
            device.Connect();
            using var destinationStream = new MemoryStream();

            // Act
            var result = await device.DownloadSdCardFileAsync("data.bin", destinationStream);

            // Assert — every byte arrived, and the transfer really did take its time getting here.
            Assert.Equal(fileData, destinationStream.ToArray());
            Assert.Equal(fileData.Length, result.FileSize);
            Assert.True(
                result.Duration >= TimeSpan.FromMilliseconds(200),
                $"Transfer completed in {result.Duration.TotalMilliseconds}ms — too fast to prove a slow transfer survives.");
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

            /// <inheritdoc cref="TestableSdCardStreamingDevice.UnterminatedAttempts"/>
            public int UnterminatedAttempts { get; set; }

            public RetryableSdCardStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            /// <summary>
            /// Reports a USB connection so these LIST retry/parsing tests are not subject to the
            /// SD-over-WiFi firmware gate (which is exercised separately by the non-USB doubles).
            /// </summary>
            public override bool IsUsbConnection => true;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                try
                {
                    // Honor the exchange's prepare phase the way the real device does: it runs first,
                    // before anything this exchange sends (#396).
                    if (prepareAsync != null)
                    {
                        await prepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var sentBefore = SentMessages.Count;
                    setupAction();
                    ExecuteTextCommandCallCount++;
                    var response = ResponseSequence.Count > 0
                        ? ResponseSequence.Dequeue()
                        : new List<string>();
                    return SdCardTestResponses.AnswerErrorQuery(
                        response, SentMessages, sentBefore, ExecuteTextCommandCallCount, UnterminatedAttempts);
                }
                finally
                {
                    // Honor the exchange's finalize phase the way the real device does: it runs
                    // however the exchange ended, still inside the exchange (#407).
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                var sentBefore = SentMessages.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                ExecuteTextCommandCallCount++;
                var response = ResponseSequence.Count > 0
                    ? ResponseSequence.Dequeue()
                    : new List<string>();
                return SdCardTestResponses.AnswerErrorQuery(
                    response, SentMessages, sentBefore, ExecuteTextCommandCallCount, UnterminatedAttempts);
            }
        }

        /// <summary>
        /// Shared device-behavior helper for the SD card fakes: models the way a live device
        /// answers the <c>SYSTem:ERRor?</c> query that <c>GetSdCardFilesAsync</c> appends to the
        /// listing exchange as an end-of-listing terminator (#396).
        /// </summary>
        private static class SdCardTestResponses
        {
            /// <summary>The reply a healthy device gives to <c>SYSTem:ERRor?</c> with a clean queue.</summary>
            public const string NoErrorReply = "0,\"No error\"";

            /// <summary>
            /// Appends the <c>SYSTem:ERRor?</c> reply to <paramref name="response"/> when the
            /// exchange actually asked for it and this attempt is not one of the
            /// <paramref name="unterminatedAttempts"/> leading attempts being simulated as
            /// unanswered.
            /// </summary>
            public static IReadOnlyList<string> AnswerErrorQuery(
                IReadOnlyList<string> response,
                IReadOnlyList<IOutboundMessage<string>> sentMessages,
                int sentBefore,
                int attemptNumber,
                int unterminatedAttempts)
            {
                if (attemptNumber <= unterminatedAttempts)
                {
                    return response;
                }

                var errorQuery = ScpiMessageProducer.GetSystemError.Data;
                var askedForError = false;
                for (var i = sentBefore; i < sentMessages.Count; i++)
                {
                    if (sentMessages[i].Data == errorQuery)
                    {
                        askedForError = true;
                        break;
                    }
                }

                if (!askedForError)
                {
                    return response;
                }

                var withTerminator = new List<string>(response) { NoErrorReply };
                return withTerminator;
            }
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that captures sent messages
        /// and returns canned text responses for ExecuteTextCommandAsync.
        /// </summary>
        private class TestableSdCardStreamingDevice : DaqifiStreamingDevice
        {
            private int _executeTextCommandCallCount;

            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public List<string> CannedTextResponse { get; set; } = new();

            /// <summary>
            /// Number of leading text exchanges to answer WITHOUT the <c>SYSTem:ERRor?</c> reply
            /// that <c>GetSdCardFilesAsync</c> uses as its end-of-listing terminator (#396) — i.e.
            /// how many attempts simulate a device that never answered, or stopped answering
            /// part-way through the listing. Defaults to 0, so the fake behaves like a healthy
            /// device and always terminates its listing.
            /// </summary>
            public int UnterminatedAttempts { get; set; }

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

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                try
                {
                    // Honor the exchange's prepare phase the way the real device does: it runs first,
                    // before anything this exchange sends (#396).
                    if (prepareAsync != null)
                    {
                        await prepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var sentBefore = SentMessages.Count;

                    // Execute the setup action so we can capture the SCPI commands
                    setupAction();
                    _executeTextCommandCallCount++;
                    return SdCardTestResponses.AnswerErrorQuery(
                        CannedTextResponse, SentMessages, sentBefore, _executeTextCommandCallCount, UnterminatedAttempts);
                }
                finally
                {
                    // Honor the exchange's finalize phase the way the real device does: it runs
                    // however the exchange ended, still inside the exchange (#407).
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                var sentBefore = SentMessages.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                _executeTextCommandCallCount++;
                return SdCardTestResponses.AnswerErrorQuery(
                    CannedTextResponse, SentMessages, sentBefore, _executeTextCommandCallCount, UnterminatedAttempts);
            }
        }

        /// <summary>
        /// Records how many exchanges were handed a finalize phase and then discards it, so a test
        /// can tell a restore that arrived through the exchange seam (#407) from one that arrived
        /// from a caller's own <c>finally</c> after the lock was released.
        /// </summary>
        private sealed class FinalizeDroppingSdCardDevice : TestableSdCardStreamingDevice
        {
            public FinalizeDroppingSdCardDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public int ExchangesOfferedAFinalizePhase { get; private set; }

            public int ExchangesWithoutAFinalizePhase { get; private set; }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                if (finalizeAsync != null)
                {
                    ExchangesOfferedAFinalizePhase++;
                }
                else
                {
                    ExchangesWithoutAFinalizePhase++;
                }

                return base.ExecuteTextCommandAsync(
                    setupAction,
                    responseTimeoutMs,
                    completionTimeoutMs,
                    cancellationToken,
                    prepareAsync,
                    finalizeAsync: null);
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

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                try
                {
                    // Honor the exchange's prepare phase the way the real device does: it runs first,
                    // before anything this exchange sends (#396).
                    if (prepareAsync != null)
                    {
                        await prepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    setupAction();
                    return CannedTextResponse;
                }
                finally
                {
                    // Honor the exchange's finalize phase the way the real device does: it runs
                    // however the exchange ended, still inside the exchange (#407).
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
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
        /// A stream that serves a different canned response (file data + EOF marker) per GET
        /// attempt, so tests can simulate a device whose SD subsystem recovers (or doesn't)
        /// across <see cref="DaqifiStreamingDevice.DownloadSdCardFileAsync(string, Stream, IProgress{SdCardTransferProgress}?, CancellationToken)"/>'s
        /// empty-transfer retry. <see cref="AttemptIndex"/> is bumped externally each time the
        /// device sends a new GET command; attempts beyond the last canned response repeat it.
        /// </summary>
        private sealed class MultiAttemptSdFileStream : Stream
        {
            private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

            private readonly byte[][] _fileDataPerAttempt;
            private int _lastServedAttempt = -1;
            private byte[] _currentBuffer = Array.Empty<byte>();
            private int _position;

            public int AttemptIndex;

            public MultiAttemptSdFileStream(params byte[][] fileDataPerAttempt)
            {
                _fileDataPerAttempt = fileDataPerAttempt;
            }

            private void PrimeForCurrentAttempt()
            {
                if (_lastServedAttempt == AttemptIndex) return;

                _lastServedAttempt = AttemptIndex;
                var index = Math.Min(AttemptIndex, _fileDataPerAttempt.Length - 1);
                var fileData = _fileDataPerAttempt[index];

                _currentBuffer = new byte[fileData.Length + EofMarker.Length];
                Array.Copy(fileData, 0, _currentBuffer, 0, fileData.Length);
                Array.Copy(EofMarker, 0, _currentBuffer, fileData.Length, EofMarker.Length);
                _position = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                PrimeForCurrentAttempt();
                var available = _currentBuffer.Length - _position;
                if (available <= 0) return 0;

                var toRead = Math.Min(count, available);
                Array.Copy(_currentBuffer, _position, buffer, offset, toRead);
                _position += toRead;
                return toRead;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// A testable device whose raw capture stream serves a different response per GET
        /// attempt (see <see cref="MultiAttemptSdFileStream"/>), for exercising
        /// <see cref="DaqifiStreamingDevice.DownloadSdCardFileAsync(string, Stream, IProgress{SdCardTransferProgress}?, CancellationToken)"/>'s
        /// empty-transfer retry.
        /// </summary>
        private class TestableRetryDownloadDevice : DaqifiStreamingDevice
        {
            private readonly MultiAttemptSdFileStream _stream;
            private int _getCommandCount;
            private int _executeTextCommandCallCount;

            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            /// <summary>
            /// Lines returned for a SD:LIS? query, so a test can seed the directory listing that
            /// <see cref="DaqifiStreamingDevice.DownloadSdCardFileAsync(string, Stream, IProgress{SdCardTransferProgress}?, CancellationToken)"/>
            /// consults for the file's reported size. The <c>SYSTem:ERRor?</c> end-of-listing
            /// terminator (#396) is appended by the fake, not seeded here.
            /// </summary>
            public List<string> ListingLines { get; } = new();

            /// <summary>
            /// Leading text exchanges to answer WITHOUT the <c>SYSTem:ERRor?</c> terminator, matching
            /// <c>TestableSdCardStreamingDevice</c>. Defaults to 0 so the fake terminates its
            /// listing like a healthy device.
            /// </summary>
            public int UnterminatedAttempts { get; set; }

            public TestableRetryDownloadDevice(params byte[][] fileDataPerAttempt)
                : base("TestDevice")
            {
                _stream = new MultiAttemptSdFileStream(fileDataPerAttempt);
            }

            public override bool IsUsbConnection => true;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                    if (stringMessage.Data.Contains("SD:GET"))
                    {
                        _stream.AttemptIndex = _getCommandCount;
                        _getCommandCount++;
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                try
                {
                    // Honor the exchange's prepare phase the way the real device does: it runs first,
                    // before anything this exchange sends (#396).
                    if (prepareAsync != null)
                    {
                        await prepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var sentBefore = SentMessages.Count;

                    setupAction();
                    _executeTextCommandCallCount++;

                    // GetSdCardFilesAsync drives the listing through THIS overload (the SPI switch is
                    // the exchange's prepareAsync phase, #406), and terminates it with SYSTem:ERRor?
                    // (#396) — so the listing has to be served here and terminated the same way
                    // TestableSdCardStreamingDevice does, or Core reads it as incomplete.
                    return SdCardTestResponses.AnswerErrorQuery(
                        ListingLines, SentMessages, sentBefore, _executeTextCommandCallCount, UnterminatedAttempts);
                }
                finally
                {
                    // Honor the exchange's finalize phase the way the real device does: it runs
                    // however the exchange ended, still inside the exchange (#407).
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                var sentBefore = SentMessages.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                _executeTextCommandCallCount++;
                return SdCardTestResponses.AnswerErrorQuery(
                    ListingLines, SentMessages, sentBefore, _executeTextCommandCallCount, UnterminatedAttempts);
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                await rawAction(_stream, cancellationToken);
            }
        }

        /// <summary>
        /// How <see cref="ParkedDownloadDevice"/> simulates a wedged transfer.
        /// </summary>
        private enum ParkMode
        {
            /// <summary>Parks in an awaited task that never observes the token (a read that never returns).</summary>
            Asynchronous,

            /// <summary>Blocks the calling thread outright, before the first await (a blocking write that never drains).</summary>
            Synchronous
        }

        /// <summary>
        /// A device whose raw-capture path parks and never observes the cancellation token it was
        /// handed — the #399 failure mode, where neither the caller's token nor a consumer-side
        /// watchdog can reach whatever the transfer is stuck in. <see cref="Release"/> unblocks the
        /// abandoned worker at the end of the test so it cannot outlive it.
        /// </summary>
        private sealed class ParkedDownloadDevice : DaqifiStreamingDevice
        {
            private readonly TaskCompletionSource _asyncPark = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _syncPark = new(false);
            private readonly ParkMode _mode;
            private readonly TimeSpan _budget;

            public ParkedDownloadDevice(ParkMode mode, TimeSpan budget)
                : base("TestDevice")
            {
                _mode = mode;
                _budget = budget;
            }

            /// <summary>Set once the transfer has actually reached the park.</summary>
            public ManualResetEventSlim Parked { get; } = new(false);

            /// <summary>
            /// When set, the next <see cref="Send{T}"/> cancels it. Lets a test cancel from inside
            /// the download's pre-flight command, i.e. between its entry guard and the SD-download
            /// gate check.
            /// </summary>
            public CancellationTokenSource? CancelOnNextSend { get; set; }

            public override bool IsUsbConnection => true;

            internal override TimeSpan SdCardDownloadTimeout => _budget;

            /// <summary>Commands this device was asked to send, in order.</summary>
            public List<string> SentCommands { get; } = new();

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    lock (SentCommands)
                    {
                        SentCommands.Add(stringMessage.Data);
                    }
                }

                // Otherwise swallowed: this device never gets as far as exchanging commands.
                var cancelSource = CancelOnNextSend;
                CancelOnNextSend = null;
                cancelSource?.Cancel();
            }

            /// <summary>Snapshot of <see cref="SentCommands"/>, safe to read while the abandoned worker runs.</summary>
            public IReadOnlyList<string> SentCommandsSnapshot()
            {
                lock (SentCommands)
                {
                    return SentCommands.ToList();
                }
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                Parked.Set();

                // Deliberately ignores cancellationToken — that is the whole point.
                if (_mode == ParkMode.Synchronous)
                {
                    _syncPark.Wait();
                    return;
                }

                await _asyncPark.Task.ConfigureAwait(false);
            }

            /// <summary>Lets the abandoned transfer finish so no thread is left parked after the test.</summary>
            public void Release()
            {
                _asyncPark.TrySetResult();
                _syncPark.Set();
            }
        }

        /// <summary>
        /// A device that serves a healthy file slowly — small chunks with a delay between them —
        /// so tests can show that bounding a stuck download does not abort a progressing one.
        /// </summary>
        private sealed class SlowDownloadDevice : DaqifiStreamingDevice
        {
            private readonly SlowChunkStream _stream;
            private readonly TimeSpan _budget;

            public SlowDownloadDevice(byte[] fileData, int chunkSize, TimeSpan chunkDelay, TimeSpan budget)
                : base("TestDevice")
            {
                _stream = new SlowChunkStream(fileData, chunkSize, chunkDelay);
                _budget = budget;
            }

            public override bool IsUsbConnection => true;

            internal override TimeSpan SdCardDownloadTimeout => _budget;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                // Not asserted on by the slow-transfer tests.
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                await rawAction(_stream, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Serves file data plus the EOF marker in small chunks, pausing between them, to imitate
        /// a large transfer trickling in over USB.
        /// </summary>
        private sealed class SlowChunkStream : Stream
        {
            private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

            private readonly byte[] _data;
            private readonly int _chunkSize;
            private readonly TimeSpan _chunkDelay;
            private int _position;

            public SlowChunkStream(byte[] fileData, int chunkSize, TimeSpan chunkDelay)
            {
                _data = new byte[fileData.Length + EofMarker.Length];
                Array.Copy(fileData, 0, _data, 0, fileData.Length);
                Array.Copy(EofMarker, 0, _data, fileData.Length, EofMarker.Length);
                _chunkSize = chunkSize;
                _chunkDelay = chunkDelay;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(_chunkDelay);
                var available = _data.Length - _position;
                if (available <= 0) return 0;

                var toRead = Math.Min(Math.Min(count, _chunkSize), available);
                Array.Copy(_data, _position, buffer, offset, toRead);
                _position += toRead;
                return toRead;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);

                var available = _data.Length - _position;
                if (available <= 0) return 0;

                var toRead = Math.Min(Math.Min(count, _chunkSize), available);
                Array.Copy(_data, _position, buffer, offset, toRead);
                _position += toRead;
                return toRead;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

        /// <summary>
        /// A testable device that reports <see cref="DaqifiStreamingDevice.IsUsbConnection"/> = false
        /// (WiFi/TCP) but can still service SD file operations (list/get/delete), so the
        /// firmware-version gate for SD-over-WiFi can be exercised on the *success* path. Mirrors
        /// <c>TestableDownloadDevice</c> but over a non-USB transport.
        /// </summary>
        private class TestableNonUsbSdCardStreamingDevice : DaqifiStreamingDevice
        {
            private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public List<string> CannedTextResponse { get; set; } = new();
            public byte[] CannedFileData { get; set; } = Array.Empty<byte>();

            public TestableNonUsbSdCardStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override bool IsUsbConnection => false;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null,
                Func<Task>? finalizeAsync = null)
            {
                try
                {
                    // Honor the exchange's prepare phase the way the real device does: it runs first,
                    // before anything this exchange sends (#396).
                    if (prepareAsync != null)
                    {
                        await prepareAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var sentBefore = SentMessages.Count;
                    setupAction();
                    return SdCardTestResponses.AnswerErrorQuery(
                        CannedTextResponse, SentMessages, sentBefore, attemptNumber: 1, unterminatedAttempts: 0);
                }
                finally
                {
                    // Honor the exchange's finalize phase the way the real device does: it runs
                    // however the exchange ended, still inside the exchange (#407).
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Func<CancellationToken, Task> setupActionAsync,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                var sentBefore = SentMessages.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                return SdCardTestResponses.AnswerErrorQuery(
                    CannedTextResponse, SentMessages, sentBefore, attemptNumber: 1, unterminatedAttempts: 0);
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                var data = new byte[CannedFileData.Length + EofMarker.Length];
                Array.Copy(CannedFileData, 0, data, 0, CannedFileData.Length);
                Array.Copy(EofMarker, 0, data, CannedFileData.Length, EofMarker.Length);

                using var fakeStream = new MemoryStream(data);
                await rawAction(fakeStream, cancellationToken);
            }
        }

        /// <summary>
        /// A WiFi/TCP device whose SD reply arrives the way the firmware actually writes it: in
        /// chunks no larger than the TCP clamp (#599), with a pause between them, and with no
        /// zero-length read to mark the end — a socket that has run out of data simply waits.
        /// </summary>
        private sealed class ChunkedWifiDownloadDevice : DaqifiStreamingDevice
        {
            private readonly byte[] _fileData;
            private readonly int _chunkSize;

            public ChunkedWifiDownloadDevice(string name, byte[] fileData, int chunkSize)
                : base(name)
            {
                _fileData = fileData;
                _chunkSize = chunkSize;
            }

            public override bool IsUsbConnection => false;

            public override void Send<T>(IOutboundMessage<T> message)
            {
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                using var fakeStream = new SlowChunkStream(_fileData, _chunkSize, TimeSpan.FromMilliseconds(1));
                await rawAction(fakeStream, cancellationToken);
            }
        }

        /// <summary>
        /// A WiFi/TCP device that acknowledges the GET and then never sends anything, and — like a
        /// socket — never returns an empty read to say so.
        /// </summary>
        private sealed class SilentWifiDownloadDevice : DaqifiStreamingDevice
        {
            public SilentWifiDownloadDevice(string name)
                : base(name)
            {
            }

            public TimeSpan IdleTimeoutOverride { get; init; } = TimeSpan.FromMilliseconds(200);

            public override bool IsUsbConnection => false;

            internal override TimeSpan SdCardTransferIdleTimeout => IdleTimeoutOverride;

            public override void Send<T>(IOutboundMessage<T> message)
            {
            }

            protected override async Task ExecuteRawCaptureAsync(
                Func<Stream, CancellationToken, Task> rawAction,
                CancellationToken cancellationToken = default)
            {
                using var fakeStream = new SilentStream();
                await rawAction(fakeStream, cancellationToken);
            }

            private sealed class SilentStream : Stream
            {
                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();
                public override long Position
                {
                    get => throw new NotSupportedException();
                    set => throw new NotSupportedException();
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                }

                public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return 0;
                }

                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }
        }
    }
}
