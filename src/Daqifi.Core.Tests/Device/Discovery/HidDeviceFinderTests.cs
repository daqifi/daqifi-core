using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class HidDeviceFinderTests
{
    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_ReturnsEmptyList()
    {
        // Arrange
        using var finder = new HidDeviceFinder();
        using var cts = new CancellationTokenSource();

        // Act
        var devices = await finder.DiscoverAsync(cts.Token);

        // Assert
        Assert.NotNull(devices);
        // Currently returns empty as HID library is not yet implemented
        Assert.Equal(0, devices.Count());
    }

    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesQuickly()
    {
        // Arrange
        using var finder = new HidDeviceFinder();
        var timeout = TimeSpan.FromSeconds(1);

        // Act
        var startTime = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotNull(devices);
        // Should complete very quickly since HID enumeration is not yet implemented
        Assert.True(elapsed.TotalSeconds < timeout.TotalSeconds);
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        // Arrange
        using var finder = new HidDeviceFinder();
        var eventRaised = false;
        finder.DiscoveryCompleted += (sender, args) => eventRaised = true;

        // Act
        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void HidDeviceFinder_Dispose_DoesNotThrow()
    {
        // Arrange
        var finder = new HidDeviceFinder();

        // Act & Assert
        finder.Dispose();
    }

    [Fact]
    public async Task HidDeviceFinder_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var finder = new HidDeviceFinder();
        finder.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }
}
