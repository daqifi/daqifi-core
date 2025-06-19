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