using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Exercises the shared lifecycle scaffolding hoisted into <see cref="DeviceFinderBase"/>
/// (issue #343) directly, via a minimal test finder — coverage the per-finder copies
/// never had as a unit (event raisers, the one failure contract every finder shares,
/// dispose idempotency, and the disposed guard).
/// </summary>
public class DeviceFinderBaseTests
{
    /// <summary>
    /// Minimal concrete finder that announces a fixed set of devices, so the base plumbing can be
    /// observed without real hardware.
    /// </summary>
    private sealed class TestFinder : DeviceFinderBase
    {
        private readonly IReadOnlyList<IDeviceInfo> _result;

        public TestFinder(params IDeviceInfo[] result) => _result = result;

        protected override Task<IReadOnlyList<IDeviceInfo>> RunPassAsync(CancellationToken passToken)
        {
            foreach (var device in _result)
            {
                OnDeviceDiscovered(device);
            }

            return Task.FromResult(_result);
        }

        // Expose the protected guard so a test can assert it flips on dispose.
        public bool IsDisposedForTest => IsDisposed;
    }

    /// <summary>
    /// A finder whose pass parks until released, so a second, timed discovery contends for the semaphore and
    /// its timeout can elapse while it is queued.
    /// </summary>
    private sealed class BlockingFinder : DeviceFinderBase
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signaled once a pass has acquired the semaphore.</summary>
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseGate() => _gate.TrySetResult();

        protected override async Task<IReadOnlyList<IDeviceInfo>> RunPassAsync(CancellationToken passToken)
        {
            Acquired.TrySetResult();
            await _gate.Task.WaitAsync(passToken).ConfigureAwait(false);
            return [];
        }
    }

    /// <summary>
    /// A finder that announces its devices and then behaves as scripted, so each way a pass can end
    /// is reproduced on the real base implementation.
    /// </summary>
    private sealed class ScriptedFinder(
        Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>> runPass) : DeviceFinderBase
    {
        /// <summary>Collects one device, then listens until the pass ends, as WiFi and mDNS do.</summary>
        public static ScriptedFinder Listening() => new(async token =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return [new DeviceInfo { SerialNumber = "SN1" }];
        });

        /// <summary>Cannot return part of an answer, as HID cannot.</summary>
        public static ScriptedFinder AllOrNothing() => new(async token =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return [new DeviceInfo { SerialNumber = "SN1" }];
        });

        protected override Task<IReadOnlyList<IDeviceInfo>> RunPassAsync(CancellationToken passToken)
            => runPass(passToken);
    }

    /// <summary>
    /// An exception whose <see cref="object.ToString"/> throws, so an isolation catch cannot
    /// render it into its trace line.
    /// </summary>
    private sealed class UnrenderableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("cannot render");
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
    public async Task OnDeviceDiscovered_ThrowingSubscriber_IsIsolatedAndDiscoveryCompletes()
    {
        var device = new DeviceInfo { Name = "Nq1", SerialNumber = "SN1" };
        using var finder = new TestFinder(device);

        // A throwing DeviceDiscovered subscriber must not abort the pass nor suppress
        // the subsequent DiscoveryCompleted event.
        finder.DeviceDiscovered += (_, _) => throw new InvalidOperationException("boom");
        var completedCount = 0;
        finder.DiscoveryCompleted += (_, _) => completedCount++;

        var result = (await finder.DiscoverAsync()).ToList();

        Assert.Single(result);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task OnDiscoveryCompleted_ThrowingSubscriber_IsIsolated()
    {
        using var finder = new TestFinder();

        finder.DiscoveryCompleted += (_, _) => throw new InvalidOperationException("boom");

        // Must not surface out of the discovery body.
        var result = await finder.DiscoverAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task OnDeviceDiscovered_SubscriberExceptionThatCannotRenderItself_IsIsolated()
    {
        var device = new DeviceInfo { Name = "Nq1", SerialNumber = "SN1" };
        using var finder = new TestFinder(device);

        // The isolation catch writes the exception into a trace line. That line has to be
        // composed inside the guard, or a throwing ToString escapes the catch.
        finder.DeviceDiscovered += (_, _) => throw new UnrenderableException();
        var completedCount = 0;
        finder.DiscoveryCompleted += (_, _) => completedCount++;

        var result = (await finder.DiscoverAsync()).ToList();

        Assert.Single(result);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task DiscoverAsync_TimeoutElapses_ReturnsWhatWasFound()
    {
        using var finder = ScriptedFinder.Listening();

        var devices = await finder.DiscoverAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("SN1", Assert.Single(devices).SerialNumber);

        // Listening to the deadline is a complete answer, so it must still age devices out.
        Assert.False(((IPartialResultReporter)finder).TakeResultsWerePartialFromLastPass());
    }

    [Fact]
    public async Task DiscoverAsync_TimeoutElapsesBeforeThePassCanAnswer_ReturnsEmptyAndIsPartial()
    {
        using var finder = ScriptedFinder.AllOrNothing();

        var devices = await finder.DiscoverAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(devices);
        Assert.True(((IPartialResultReporter)finder).TakeResultsWerePartialFromLastPass());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_Cancelled_ThrowsWhetherOrNotThePassCanAnswerInPart(bool listening)
    {
        using var finder = listening ? ScriptedFinder.Listening() : ScriptedFinder.AllOrNothing();
        using var cts = new CancellationTokenSource();
        var completed = 0;
        finder.DiscoveryCompleted += (_, _) => Interlocked.Increment(ref completed);

        var pass = finder.DiscoverAsync(TimeSpan.FromMinutes(5), cts.Token);
        Assert.False(pass.IsCompleted);
        cts.Cancel();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pass.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cts.Token, canceled.CancellationToken);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task DiscoverAsync_CancelledWhileQueuedBehindAnotherPass_ThrowsAndLeavesThatPassAlone()
    {
        using var finder = new BlockingFinder();
        var first = finder.DiscoverAsync();
        await finder.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var queued = finder.DiscoverAsync(cancellationToken: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        finder.ReleaseGate();
        Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(5)));

        // The queued call never held the pass lock, so it must not have released it either.
        Assert.Empty(await finder.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DiscoverAsync_TransportFails_ThrowsThatRaisesCompletedAndFreesTheFinder()
    {
        var fail = true;
        using var finder = new ScriptedFinder(_ => fail
            ? throw new IOException("enumerating ports failed")
            : Task.FromResult<IReadOnlyList<IDeviceInfo>>([]));
        var completed = 0;
        finder.DiscoveryCompleted += (_, _) => completed++;

        await Assert.ThrowsAsync<IOException>(() => finder.DiscoverAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, completed);

        fail = false;
        Assert.Empty(await finder.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DiscoverAsync_InvalidTimeout_Throws()
    {
        using var finder = new TestFinder();

        foreach (var timeout in new[] { TimeSpan.FromMilliseconds(-2), TimeSpan.MaxValue })
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => finder.DiscoverAsync(timeout));
            Assert.Equal("timeout", ex.ParamName);
        }

        Assert.Empty(await finder.DiscoverAsync(Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task DiscoverAsync_PartialFlag_DescribesOnlyTheLastPass()
    {
        var truncate = true;
        using var finder = new ScriptedFinder(async token =>
        {
            if (truncate)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return [];
        });

        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(20));
        truncate = false;
        await finder.DiscoverAsync();

        Assert.False(((IPartialResultReporter)finder).TakeResultsWerePartialFromLastPass());
    }

    [Fact]
    public async Task DiscoverAsync_Timeout_WhileAwaitingConcurrentPass_ReturnsEmptyNotCanceled()
    {
        using var finder = new BlockingFinder();

        // First pass acquires the discovery semaphore and parks (uncancelable token).
        var first = finder.DiscoverAsync();
        // Bounded wait: if the first pass faults before signaling acquisition, fail fast
        // instead of hanging until the runner-level timeout.
        await finder.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the semaphore is now held

        // A second, timed pass must wait on the held semaphore; its timeout elapses
        // first. The timeout is a normal terminal condition, so it must complete with
        // an empty result rather than throwing OperationCanceledException (which would
        // bubble through DaqifiTools.GuardAsync as a canceled tool call).
        var result = await finder.DiscoverAsync(TimeSpan.FromMilliseconds(50));
        Assert.Empty(result);

        // The first pass is untouched by the second's timeout.
        finder.ReleaseGate();
        Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DiscoverAsync_Timeout_WhileAwaitingConcurrentPass_RaisesDiscoveryCompleted()
    {
        using var finder = new BlockingFinder();

        var completedCount = 0;
        finder.DiscoveryCompleted += (_, _) => Interlocked.Increment(ref completedCount);

        // First pass acquires the semaphore and parks; it has NOT completed yet.
        var first = finder.DiscoverAsync();
        await finder.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A timed pass whose timeout elapses while awaiting the held semaphore must
        // still raise the end-of-pass signal, matching a normal timed pass — otherwise
        // a consumer awaiting DiscoveryCompleted could wait indefinitely.
        var result = await finder.DiscoverAsync(TimeSpan.FromMilliseconds(50));
        Assert.Empty(result);

        // The parked first pass hasn't completed, so this completion is the timed
        // pass's own end-of-pass signal.
        Assert.Equal(1, Volatile.Read(ref completedCount));

        finder.ReleaseGate();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_WhileAPassRunsAndAnotherWaits_LetsBothFinish()
    {
        // Disposing used to dispose the discovery semaphore under them: the running pass's
        // finally Release threw ObjectDisposedException in place of its results, and the
        // waiting pass was never woken.
        var finder = new BlockingFinder();
        var running = finder.DiscoverAsync();
        await finder.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = finder.DiscoverAsync();

        finder.Dispose();
        finder.ReleaseGate();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_ConcurrentCalls_AllSucceed()
    {
        var finder = new TestFinder();

        // Concurrent Dispose() calls must resolve to a single winner (Interlocked flag) and
        // none of them may fault.
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => finder.Dispose())).ToArray();
        var ex = Record.Exception(() => Task.WaitAll(tasks));

        Assert.Null(ex);
        Assert.True(finder.IsDisposedForTest);
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
