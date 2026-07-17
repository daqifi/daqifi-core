using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class WifiBridgeActivatorTests
{
    [Fact]
    public void Activate_NullPortName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WifiBridgeActivator.Activate(null!));
    }

    [Fact]
    public void Activate_AlreadyCancelled_ThrowsBeforeOpeningPort()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Use a bogus port name; if cancellation isn't honored up-front the test would
        // surface a port-open failure instead of OperationCanceledException.
        Assert.Throws<OperationCanceledException>(
            () => WifiBridgeActivator.Activate("COM999", cts.Token));
    }

    [Fact]
    public void Activate_InvalidPort_Throws()
    {
        Assert.ThrowsAny<Exception>(() => WifiBridgeActivator.Activate("COM999"));
    }

    [Fact]
    public void Deactivate_NullPortName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WifiBridgeActivator.Deactivate(null!));
    }

    [Fact]
    public void Deactivate_AlreadyCancelled_ThrowsBeforeOpeningPort()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Use a bogus port name; if cancellation isn't honored up-front the test would
        // surface a port-open failure instead of OperationCanceledException.
        Assert.Throws<OperationCanceledException>(
            () => WifiBridgeActivator.Deactivate("COM999", cts.Token));
    }

    [Fact]
    public void Deactivate_InvalidPort_Throws()
    {
        Assert.ThrowsAny<Exception>(() => WifiBridgeActivator.Deactivate("COM999"));
    }
}
