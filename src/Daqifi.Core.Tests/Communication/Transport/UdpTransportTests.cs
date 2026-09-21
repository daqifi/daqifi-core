using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Tests.TestSupport;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Transport;

public class UdpTransportTests
{
    [Fact]
    public void Constructor_ShouldCreateInstance()
    {
        // Act
        using var transport = new UdpTransport();

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void Constructor_WithPort_ShouldCreateInstance()
    {
        // Act
        using var transport = new UdpTransport(30303);

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task OpenAsync_ShouldOpenTransport()
    {
        // Arrange
        using var transport = new UdpTransport(0); // Use any available port
        var statusChanged = false;
        transport.StatusChanged += (sender, args) =>
        {
            if (args.IsConnected) statusChanged = true;
        };

        // Act
        await transport.OpenAsync();

        // Assert
        Assert.True(transport.IsOpen);
        Assert.True(statusChanged);
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseTransport()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var closedStatusChanged = false;
        transport.StatusChanged += (sender, args) =>
        {
            if (!args.IsConnected) closedStatusChanged = true;
        };

        // Act
        await transport.CloseAsync();

        // Assert
        Assert.False(transport.IsOpen);
        Assert.True(closedStatusChanged);
    }

    // Sandboxes, missing routes, and send-buffer exhaustion are not UdpTransport defects;
    // swallowing the SocketException used to report passed with zero asserts. The gate
    // records Skipped instead. AccessDenied from UdpTransport itself still fails — that
    // is EnableBroadcast not being set, which is the bug these two exist to catch.
    [UdpBroadcastFact(UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable)]
    public async Task SendBroadcastAsync_ShouldSendData()
    {
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("DAQiFi?\r\n");

        await UdpBroadcastFactAttribute.AwaitSendAsync(
            () => transport.SendBroadcastAsync(testData, 30303));
    }

    [UdpBroadcastFact(UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable)]
    public async Task SendBroadcastAsync_WithEndpoint_ShouldSendData()
    {
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("DAQiFi?\r\n");
        var endPoint = new IPEndPoint(IPAddress.Broadcast, 30303);

        await UdpBroadcastFactAttribute.AwaitSendAsync(
            () => transport.SendBroadcastAsync(testData, endPoint));
    }

    [Fact]
    public async Task SendUnicastAsync_ShouldSendData()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("Test");
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);

        // Act & Assert (should not throw)
        await transport.SendUnicastAsync(testData, endpoint);
    }

    [Fact]
    public async Task ReceiveAsync_WithTimeout_ShouldReturnNullOnTimeout()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act
        var result = await transport.ReceiveAsync(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SendAndReceive_Loopback_ShouldWork()
    {
        // Arrange - use dynamic port allocation to avoid conflicts in parallel test runs
        using var receiver = new UdpTransport(0);
        using var sender = new UdpTransport(0);

        await receiver.OpenAsync();
        await sender.OpenAsync();

        // Get the dynamically assigned port after opening
        var receiverPort = receiver.LocalPort;

        var testData = Encoding.ASCII.GetBytes("Hello UDP!");
        var endpoint = new IPEndPoint(IPAddress.Loopback, receiverPort);

        // Act
        await sender.SendUnicastAsync(testData, endpoint);
        var result = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testData, result.Value.Data);
        Assert.Equal(IPAddress.Loopback, result.Value.RemoteEndPoint.Address);
    }

    [Fact]
    public async Task OpenAsync_WhenAlreadyOpen_ShouldNotThrow()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act & Assert
        await transport.OpenAsync(); // Should not throw
        Assert.True(transport.IsOpen);
    }

    [Fact]
    public async Task SendBroadcastAsync_WhenNotOpen_ShouldThrow()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        var testData = Encoding.ASCII.GetBytes("Test");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.SendBroadcastAsync(testData, 30303));
    }

    [Fact]
    public async Task Dispose_ShouldCloseTransport()
    {
        // Arrange
        var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act
        transport.Dispose();

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task ConnectionInfo_ShouldReflectStatus()
    {
        // Arrange - use dynamic port to avoid conflicts in parallel test runs
        using var transport = new UdpTransport(0);

        // Act & Assert
        Assert.Contains("Closed", transport.ConnectionInfo);

        await transport.OpenAsync();
        Assert.Contains("Open", transport.ConnectionInfo);
    }
}
