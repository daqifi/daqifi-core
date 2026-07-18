using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceWithMessageProducerTests
{
    private sealed class BinaryOutboundMessage : IOutboundMessage<byte[]>
    {
        public byte[] Data { get; set; }

        public BinaryOutboundMessage(byte[] data)
        {
            Data = data;
        }

        public byte[] GetBytes() => Data;
    }

    [Fact]
    public void DaqifiDevice_WithStream_ShouldInitializeMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        
        // Act
        using var device = new DaqifiDevice("Test Device", stream, IPAddress.Loopback);
        
        // Assert
        Assert.Equal("Test Device", device.Name);
        Assert.Equal(IPAddress.Loopback, device.IpAddress);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_Connect_ShouldStartMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        
        // Act
        device.Connect();
        
        // Assert
        Assert.True(device.IsConnected);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenConnected_ShouldWriteToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        
        // Act
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        
        // Wait for background thread to process the message
        Thread.Sleep(200);
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("SYSTem:SYSInfoPB?", written);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenDisconnected_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
    }

    [Fact]
    public void DaqifiDevice_SendNonStringMessage_WhenConnected_WritesDirectlyToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        device.Send(new BinaryOutboundMessage(payload));

        // Assert - non-string payloads bypass the queued producer and are written synchronously.
        Assert.Equal(payload, stream.ToArray());
    }

    [Fact]
    public void DaqifiDevice_ProducerlessConstructor_SendAnyMessage_ThrowsInvalidOperationException()
    {
        // Arrange - the (name, ipAddress) constructor has no transport or stream to send on.
        using var device = new DaqifiDevice("Test Device", IPAddress.Loopback);
        device.Connect();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
        Assert.Contains("no transport or stream", ex.Message);
    }

    [Fact]
    public void DaqifiDevice_Disconnect_ShouldStopMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        
        // Act
        device.Disconnect();
        
        // Assert
        Assert.False(device.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_StatusChanged_ShouldFireEvent()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        ConnectionStatus? capturedStatus = null;
        
        device.StatusChanged += (sender, args) => capturedStatus = args.Status;
        
        // Act
        device.Connect();
        
        // Assert
        Assert.Equal(ConnectionStatus.Connected, capturedStatus);
    }
}