using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiStreamingDeviceTests
    {
        [Fact]
        public void Constructor_InitializesStreamingFrequency()
        {
            // Arrange & Act
            var device = new DaqifiStreamingDevice("TestDevice");

            // Assert
            Assert.Equal(100, device.StreamingFrequency);
            Assert.False(device.IsStreaming);
        }

        [Fact]
        public void StartStreaming_WhenConnected_SendsCorrectCommandAndSetsIsStreaming()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            device.StreamingFrequency = 200;

            // Act
            device.StartStreaming();

            // Assert
            Assert.True(device.IsStreaming);
            var sentMessage = Assert.Single(device.SentMessages);
            var expectedMessage = ScpiMessageProducer.StartStreaming(200);
            Assert.Equal(expectedMessage.Data, sentMessage.Data);
        }

        [Fact]
        public void StopStreaming_WhenConnected_SendsCorrectCommandAndSetsIsStreaming()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            device.StartStreaming();
            device.SentMessages.Clear();

            // Act
            device.StopStreaming();

            // Assert
            Assert.False(device.IsStreaming);
            var sentMessage = Assert.Single(device.SentMessages);
            var expectedMessage = ScpiMessageProducer.StopStreaming;
            Assert.Equal(expectedMessage.Data, sentMessage.Data);
        }

        [Fact]
        public void StartStreaming_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<System.InvalidOperationException>(() => device.StartStreaming());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public void StopStreaming_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<System.InvalidOperationException>(() => device.StopStreaming());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        // ---------------------------------------------------------------------
        // ADC calibration & voltage-precision NVM persistence (daqifi-core#207)
        // ---------------------------------------------------------------------

        public static IEnumerable<object[]> NvmPersistenceCommands()
        {
            yield return new object[] { "SaveAdcCalibration", "CONFigure:ADC:SAVEcal" };
            yield return new object[] { "LoadAdcCalibration", "CONFigure:ADC:LOADcal" };
            yield return new object[] { "SaveVoltagePrecision", "CONFigure:VOLTage:SAVE" };
            yield return new object[] { "LoadVoltagePrecision", "CONFigure:VOLTage:LOAD" };
        }

        private static void InvokeNvmMethod(IStreamingDevice device, string methodName)
        {
            switch (methodName)
            {
                case "SaveAdcCalibration": device.SaveAdcCalibration(); break;
                case "LoadAdcCalibration": device.LoadAdcCalibration(); break;
                case "SaveVoltagePrecision": device.SaveVoltagePrecision(); break;
                case "LoadVoltagePrecision": device.LoadVoltagePrecision(); break;
                default: throw new System.ArgumentOutOfRangeException(nameof(methodName), methodName, null);
            }
        }

        [Theory]
        [MemberData(nameof(NvmPersistenceCommands))]
        public void NvmPersistence_WhenConnected_SendsCorrectCommand(string methodName, string expectedCommand)
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            // Act
            InvokeNvmMethod(device, methodName);

            // Assert
            var sentMessage = Assert.Single(device.SentMessages);
            Assert.Equal(expectedCommand, sentMessage.Data);
        }

        [Theory]
        [MemberData(nameof(NvmPersistenceCommands))]
        public void NvmPersistence_WhenDisconnected_ThrowsInvalidOperationException(string methodName, string expectedCommand)
        {
            // Arrange
            _ = expectedCommand;
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<System.InvalidOperationException>(() => InvokeNvmMethod(device, methodName));
            Assert.Equal("Device is not connected.", exception.Message);
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that captures sent messages.
        /// </summary>
        private class TestableDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            public TestableDaqifiStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                // Override to capture the message instead of sending it.
                // This avoids the base class's check for a real connection.
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }
        }
    }
} 