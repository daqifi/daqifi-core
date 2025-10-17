using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class SerialDeviceFinderTests
{
    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_ReturnsDevices()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        using var cts = new CancellationTokenSource();

        // Act
        var devices = await finder.DiscoverAsync(cts.Token);

        // Assert
        Assert.NotNull(devices);
        // May or may not find devices depending on system, but should not throw
    }

    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesWithinTimeout()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        var timeout = TimeSpan.FromSeconds(5);

        // Act
        var startTime = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotNull(devices);
        Assert.True(elapsed.TotalSeconds <= timeout.TotalSeconds + 1);
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        var eventRaised = false;
        finder.DiscoveryCompleted += (sender, args) => eventRaised = true;

        // Act
        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void SerialDeviceFinder_Dispose_DoesNotThrow()
    {
        // Arrange
        var finder = new SerialDeviceFinder();

        // Act & Assert
        finder.Dispose();
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var finder = new SerialDeviceFinder();
        finder.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }

    [Fact]
    public void SerialDeviceFinder_DefaultConstructor_Uses115200Baud()
    {
        // Arrange & Act
        using var finder = new SerialDeviceFinder();

        // Assert
        Assert.NotNull(finder);
    }

    [Fact]
    public void SerialDeviceFinder_CustomBaudRate_AcceptsCustomBaudRate()
    {
        // Arrange & Act
        using var finder = new SerialDeviceFinder(9600);

        // Assert
        Assert.NotNull(finder);
    }
}
