using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceWithMessageProducerTests
{
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