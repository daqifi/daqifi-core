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

    [Fact]
    public async Task ActivateAsync_NullPortName_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => WifiBridgeActivator.ActivateAsync(null!));
    }

    [Fact]
    public async Task ActivateAsync_AlreadyCancelled_ThrowsBeforeOpeningPort()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => WifiBridgeActivator.ActivateAsync("COM999", cts.Token));
    }

    [Fact]
    public async Task ActivateAsync_InvalidPort_PropagatesUnderlyingException()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => WifiBridgeActivator.ActivateAsync("COM999"));
    }

    [Fact]
    public async Task DeactivateAsync_NullPortName_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => WifiBridgeActivator.DeactivateAsync(null!));
    }

    [Fact]
    public async Task DeactivateAsync_AlreadyCancelled_ThrowsBeforeOpeningPort()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => WifiBridgeActivator.DeactivateAsync("COM999", cts.Token));
    }

    [Fact]
    public async Task DeactivateAsync_InvalidPort_PropagatesUnderlyingException()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => WifiBridgeActivator.DeactivateAsync("COM999"));
    }

    [Fact]
    public async Task RunWithHardTimeoutAsync_OperationCompletes_Succeeds()
    {
        var ran = false;

        await WifiBridgeActivator.RunWithHardTimeoutAsync(
            () => ran = true,
            "TestOp",
            CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task RunWithHardTimeoutAsync_OperationThrows_PropagatesException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WifiBridgeActivator.RunWithHardTimeoutAsync(
                () => throw new InvalidOperationException("boom"),
                "TestOp",
                CancellationToken.None));
    }

    [Fact]
    public async Task RunWithHardTimeoutAsync_UncancellableHang_ThrowsTimeoutExceptionAndAbandonsTask()
    {
        var originalTimeout = WifiBridgeActivator.HardTimeoutMs;
        WifiBridgeActivator.HardTimeoutMs = 50;
        try
        {
            var released = new ManualResetEventSlim(false);

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                WifiBridgeActivator.RunWithHardTimeoutAsync(
                    () =>
                    {
                        // Simulates SerialPort.Open()'s uncancellable native hang: this
                        // blocking wait ignores the hard timeout entirely and only
                        // completes when the test releases it below.
                        released.Wait();
                        throw new InvalidOperationException("late failure after abandonment");
                    },
                    "TestOp",
                    CancellationToken.None));

            Assert.Contains("TestOp", ex.Message);
            Assert.Contains("50ms", ex.Message);

            // Let the abandoned worker task finish so its continuation observes the
            // fault; if this were left unobserved it would surface as an
            // UnobservedTaskException on GC, which the isolation pattern must avoid.
            released.Set();
            await Task.Delay(50);
        }
        finally
        {
            WifiBridgeActivator.HardTimeoutMs = originalTimeout;
        }
    }

    [Fact]
    public async Task RunWithHardTimeoutAsync_CallerCancelledBeforeTimeout_ThrowsOperationCanceledException()
    {
        var originalTimeout = WifiBridgeActivator.HardTimeoutMs;
        WifiBridgeActivator.HardTimeoutMs = 5000;
        try
        {
            using var cts = new CancellationTokenSource();
            var released = new ManualResetEventSlim(false);

            var runTask = WifiBridgeActivator.RunWithHardTimeoutAsync(
                () =>
                {
                    released.Wait();
                },
                "TestOp",
                cts.Token);

            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

            released.Set();
        }
        finally
        {
            WifiBridgeActivator.HardTimeoutMs = originalTimeout;
        }
    }
}
