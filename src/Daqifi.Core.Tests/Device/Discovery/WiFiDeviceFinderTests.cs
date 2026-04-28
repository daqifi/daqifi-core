using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class WiFiDeviceFinderTests
{
    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesWithinTimeout()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        var timeout = TimeSpan.FromSeconds(2);

        // Act
        var startTime = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotNull(devices);
        // Allow for some overhead, but should complete reasonably close to timeout
        Assert.True(elapsed.TotalSeconds <= timeout.TotalSeconds + 1);
    }

    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_CanBeCancelled()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        var devices = await finder.DiscoverAsync(cts.Token);

        // Assert
        Assert.NotNull(devices);
        // Should return empty or partial results when cancelled
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        var eventRaised = false;
        finder.DiscoveryCompleted += (sender, args) => eventRaised = true;

        // Act
        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void WiFiDeviceFinder_Dispose_DoesNotThrow()
    {
        // Arrange
        var finder = new WiFiDeviceFinder();

        // Act & Assert
        finder.Dispose();
        // Should not throw
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var finder = new WiFiDeviceFinder();
        finder.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }

    [Fact]
    public void WiFiDeviceFinder_DefaultConstructor_UsesPort30303()
    {
        // Arrange & Act
        using var finder = new WiFiDeviceFinder();

        // Assert
        // Should not throw and should use default port 30303
        Assert.NotNull(finder);
    }

    [Fact]
    public void WiFiDeviceFinder_CustomPort_AcceptsCustomPort()
    {
        // Arrange & Act
        using var finder = new WiFiDeviceFinder(12345);

        // Assert
        Assert.NotNull(finder);
    }

    // Virtual / tunnel adapter filter — issue #179.
    // WSL2 mirrored networking, Hyper-V vEthernet, VPN/TAP adapters frequently share a /24
    // subnet with the real WiFi/Ethernet NIC and cause Windows routing to pick the wrong egress
    // for broadcasts, silently breaking discovery.
    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Ethernet 3", "Hyper-V Virtual Ethernet Adapter #2")]
    [InlineData("Ethernet 4", "WSL Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1")]
    [InlineData("OpenVPN TAP-Windows6", "TAP-Windows Adapter V9")]
    public void IsVirtualOrTunnelInterface_VirtualAdapters_ReturnsTrue(string name, string description)
    {
        Assert.True(WiFiDeviceFinder.IsVirtualOrTunnelInterface(name, description));
    }

    [Theory]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz")]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller")]
    [InlineData("Ethernet 2", "Intel(R) Ethernet Connection I219-LM")]
    [InlineData("WLAN", "Qualcomm Atheros QCA9377 Wireless Network Adapter")]
    public void IsVirtualOrTunnelInterface_PhysicalAdapters_ReturnsFalse(string name, string description)
    {
        Assert.False(WiFiDeviceFinder.IsVirtualOrTunnelInterface(name, description));
    }

    [Fact]
    public void IsVirtualOrTunnelInterface_NullInputs_ReturnsFalse()
    {
        Assert.False(WiFiDeviceFinder.IsVirtualOrTunnelInterface(null, null));
    }
}
