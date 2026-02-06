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
        public async Task StartSdCardLoggingAsync_SendsCorrectCommandSequence()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.StartSdCardLoggingAsync("mylog.bin");

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(4, sentCommands.Count);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:LOGging \"mylog.bin\"", sentCommands[1]);
            Assert.Equal("SYSTem:STReam:FORmat 0", sentCommands[2]);
            Assert.Equal("SYSTem:StartStreamData 100", sentCommands[3]);
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
            Assert.Contains("SYSTem:STORage:SD:LOGging \"custom_data.bin\"", sentCommands);
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
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:LOGging"));
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
            Assert.Equal(2, sentCommands.Count);
            Assert.Equal("SYSTem:StopStreamData", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:ENAble 0", sentCommands[1]);
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
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:LOGging"));
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
            var loggingCommand = sentCommands.FirstOrDefault(c => c.StartsWith("SYSTem:STORage:SD:LOGging"));
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
        public async Task FormatSdCardAsync_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableSdCardStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.FormatSdCardAsync();

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(2, sentCommands.Count);
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[0]);
            Assert.Equal("SYSTem:STORage:SD:FORmat", sentCommands[1]);
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
            // Arrange — use the testable device which has no transport (simulates non-USB)
            var device = new TestableSdCardStreamingDevice("TestDevice");
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
        /// A testable version of DaqifiStreamingDevice that captures sent messages
        /// and returns canned text responses for ExecuteTextCommandAsync.
        /// </summary>
        private class TestableSdCardStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public List<string> CannedTextResponse { get; set; } = new();

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
                CancellationToken cancellationToken = default)
            {
                // Execute the setup action so we can capture the SCPI commands
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse);
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
                CancellationToken cancellationToken = default)
            {
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse);
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
    }
}
