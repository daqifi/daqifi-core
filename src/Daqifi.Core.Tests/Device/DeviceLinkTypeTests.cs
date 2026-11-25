using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Net;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// How a device learns its link type (issue #612) and where its network address lives
/// (issue #616).
/// </summary>
public class DeviceLinkTypeTests
{
    [Fact]
    public void SerialTransport_IsAUsbConnection()
    {
        using IStreamTransport transport = new SerialStreamTransport("COM_TEST_612");

        Assert.True(transport.IsUsbConnection);
    }

    [Fact]
    public void TcpTransport_IsNotAUsbConnection()
    {
        using IStreamTransport transport = new TcpStreamTransport(IPAddress.Loopback, 1);

        Assert.False(transport.IsUsbConnection);
    }

    [Fact]
    public void CustomTransport_DefaultsToNotUsb()
    {
        using var device = new DaqifiStreamingDevice("Device", new LinkTransport());

        Assert.False(device.IsUsbConnection);
    }

    [Fact]
    public void CustomUsbTransport_MakesTheDeviceAUsbConnection()
    {
        // Before #612 the device type-tested for SerialStreamTransport, so a consumer's own
        // transport over a USB link was treated as a network link.
        using var device = new DaqifiStreamingDevice("Device", new LinkTransport { Usb = true });

        Assert.True(device.IsUsbConnection);
    }

    [Fact]
    public void SerialTransport_MakesTheDeviceAUsbConnection()
    {
        using var device = new DaqifiStreamingDevice("Device", new SerialStreamTransport("COM_TEST_612"));

        Assert.True(device.IsUsbConnection);
    }

    [Fact]
    public void DeviceWithoutTransport_IsNotAUsbConnection()
    {
        using var device = new TransportlessStreamingDevice("Device");

        Assert.False(device.IsUsbConnection);
    }

    [Fact]
    public void DaqifiDevice_DoesNotClaimTheNetworkFacet()
    {
        // DaqifiDevice serves every transport, so implementing the facet would make a USB device
        // claim a network address. Only the factory's TCP device implements it (issue #616); see
        // DaqifiDeviceFactoryTests for those paths.
        using var streamingDevice = new DaqifiStreamingDevice("Device", new SerialStreamTransport("COM_TEST_616"));
        IDevice device = streamingDevice;

        Assert.False(device is INetworkAddressedDevice);
    }

    private sealed class LinkTransport : IStreamTransport
    {
        public bool Usb { get; init; }

        public bool IsUsbConnection => Usb;

        public Stream Stream { get; } = new MemoryStream();
        public bool IsConnected => false;
        public string ConnectionInfo => "Link test";

#pragma warning disable CS0067 // Never raised: these tests never connect the transport.
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Stream.Dispose();
    }
}
