using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    }
}
