using Daqifi.Core.Device;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// The asynchronous lifecycle members are reachable through <see cref="IDevice"/> (issue #460).
/// The confirming members of <see cref="IStreamingDevice"/> are covered by
/// <see cref="DaqifiStreamingDeviceConfirmationTests"/>.
/// </summary>
public class DaqifiStreamingDeviceAsyncSurfaceTests
{
    #region IDevice.ConnectAsync / DisconnectAsync are genuine members, not DIM shims

    [Fact]
    public async Task IDevice_ConnectAsync_DisconnectAsync_AreCallableThroughTheInterface()
    {
        IDevice device = new TransportlessStreamingDevice("TestDevice");

        await device.ConnectAsync();
        Assert.True(device.IsConnected);

        await device.DisconnectAsync();
        Assert.False(device.IsConnected);
    }

    #endregion

    #region IDevice : IAsyncDisposable

    [Fact]
    public async Task IDevice_IsAsyncDisposable_AndDisposeAsyncDisconnects()
    {
        IDevice device = new TransportlessStreamingDevice("TestDevice");
        await device.ConnectAsync();

        await device.DisposeAsync();

        Assert.False(device.IsConnected);
    }

    #endregion
}
