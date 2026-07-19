using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Exercises the shared lifecycle scaffolding hoisted into <see cref="DeviceFinderBase"/>
/// (issue #343) directly, via a minimal test finder — coverage the per-finder copies
/// never had as a unit (event raisers, the timeout overload's token routing, dispose
/// idempotency, and the disposed guard).
/// </summary>
public class DeviceFinderBaseTests
{
    /// <summary>
    /// Minimal concrete finder that records the token it was invoked with and raises
    /// the base events, so the base plumbing can be observed without real hardware.
    /// </summary>
    private sealed class TestFinder : DeviceFinderBase
    {
        private readonly IReadOnlyList<IDeviceInfo> _result;

        public TestFinder(params IDeviceInfo[] result) => _result = result;

        public CancellationToken LastToken { get; private set; }
        public int DiscoverCallCount { get; private set; }

        public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            DiscoverCallCount++;
            LastToken = cancellationToken;

            await DiscoverySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var device in _result)
                {
                    OnDeviceDiscovered(device);
                }

                OnDiscoveryCompleted();
                return _result;
            }
            finally
            {
                DiscoverySemaphore.Release();
            }
        }

        // Expose the protected guard so a test can assert it flips on dispose.
        public bool IsDisposedForTest => IsDisposed;
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDeviceDiscoveredAndDiscoveryCompleted()
    {
        var device = new DeviceInfo { Name = "Nq1", SerialNumber = "SN1" };
        using var finder = new TestFinder(device);

        var discovered = new List<IDeviceInfo>();
        var completedCount = 0;
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DiscoveryCompleted += (_, _) => completedCount++;

        var result = (await finder.DiscoverAsync()).ToList();

        Assert.Single(result);
        Assert.Single(discovered);
        Assert.Same(device, discovered[0]);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task DiscoverAsync_Timeout_RoutesThroughCancellationTokenOverload()
    {
        using var finder = new TestFinder();

        await finder.DiscoverAsync(TimeSpan.FromSeconds(30));

        // The timeout overload must delegate to the token overload with a real,
        // cancelable token wired to the timeout (not CancellationToken.None).
        Assert.Equal(1, finder.DiscoverCallCount);
        Assert.True(finder.LastToken.CanBeCanceled);
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var finder = new TestFinder();
        finder.Dispose();

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
        // ThrowIfDisposed names the concrete runtime type, not the base.
        Assert.Equal(nameof(TestFinder), ex.ObjectName);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var finder = new TestFinder();

        finder.Dispose();
        Assert.True(finder.IsDisposedForTest);

        // Second dispose must be a no-op (no ObjectDisposedException from re-disposing
        // the semaphore).
        finder.Dispose();
        Assert.True(finder.IsDisposedForTest);
    }
}
