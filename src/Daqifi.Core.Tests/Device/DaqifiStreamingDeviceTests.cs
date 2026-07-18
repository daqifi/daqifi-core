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

        [Theory]
        [InlineData(1)]
        [InlineData(500)]
        [InlineData(1000)] // MaxSamplingRate for all Nyquist types
        public void StreamingFrequency_WithinRange_IsAccepted(int frequency)
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act
            device.StreamingFrequency = frequency;

            // Assert
            Assert.Equal(frequency, device.StreamingFrequency);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1001)] // MaxSamplingRate + 1
        [InlineData(50000)]
        public void StreamingFrequency_OutOfRange_ThrowsArgumentOutOfRangeException(int frequency)
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<System.ArgumentOutOfRangeException>(() => device.StreamingFrequency = frequency);
            Assert.Contains("1000", exception.Message); // valid range surfaced from DeviceCapabilities.MaxSamplingRate
        }

        [Fact]
        public void StreamingFrequency_OutOfRange_LeavesPreviousValueUnchanged()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice") { StreamingFrequency = 250 };

            // Act
            Assert.Throws<System.ArgumentOutOfRangeException>(() => device.StreamingFrequency = 5000);

            // Assert
            Assert.Equal(250, device.StreamingFrequency);
        }

        [Fact]
        public void StreamingFrequency_UsesCapabilitiesMaxSamplingRate_NotAHardcodedConstant()
        {
            // Arrange: lower the device's advertised max and confirm the guard tracks it.
            var device = new DaqifiStreamingDevice("TestDevice");
            device.Metadata.Capabilities.MaxSamplingRate = 200;

            // Act & Assert: 200 is now the ceiling, 201 is rejected.
            device.StreamingFrequency = 200;
            Assert.Equal(200, device.StreamingFrequency);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => device.StreamingFrequency = 201);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void StreamingFrequency_InvalidCapabilitiesMax_SanitizesCeilingToOne(int invalidMax)
        {
            // Arrange: MaxSamplingRate is a mutable, unvalidated public property. An invalid value
            // must not produce an impossible range that rejects every frequency.
            var device = new DaqifiStreamingDevice("TestDevice");
            device.Metadata.Capabilities.MaxSamplingRate = invalidMax;

            // Act & Assert: the ceiling is sanitized to 1, so 1 is accepted and 2 is rejected.
            device.StreamingFrequency = 1;
            Assert.Equal(1, device.StreamingFrequency);

            var ex = Assert.Throws<System.ArgumentOutOfRangeException>(() => device.StreamingFrequency = 2);
            Assert.Contains("1 and 1", ex.Message); // range reported with the sanitized max, not "1..0"
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