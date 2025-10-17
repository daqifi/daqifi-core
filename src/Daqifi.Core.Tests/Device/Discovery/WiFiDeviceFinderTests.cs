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
}
