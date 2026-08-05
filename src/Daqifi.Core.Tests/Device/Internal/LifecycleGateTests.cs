using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="LifecycleGate"/>, the connect-against-disconnect serialization
/// extracted from <c>DaqifiDevice</c> (issue #379).
/// </summary>
/// <remarks>
/// The gate's behaviour through the device is already covered by <c>DeviceReconnectTests</c>,
/// which races a real reconnect loop against a caller's connect over a scripted transport. These
/// exercise the collaborator directly, which is what the extraction newly makes possible: no
/// device, no transport, no reconnect loop, so contention is produced deliberately rather than
/// raced into, and the policies that were previously only observable through their side effects on
/// a device can be asserted on their own.
/// </remarks>
public class LifecycleGateTests
{
    /// <summary>How long a test waits for something that should already have happened.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private static LifecycleGate Create(
        ILogger? logger = null,
        string name = "Nq1",
        TimeSpan? connectTimeout = null,
        TimeSpan? teardownTimeout = null)
        => new(
            logger ?? NullLogger.Instance,
            () => name,
            () => connectTimeout ?? TimeSpan.FromMilliseconds(100),
            () => teardownTimeout ?? TimeSpan.FromMilliseconds(150));

    /// <summary>
    /// Parks an operation inside the gate's critical section and returns a handle that releases it.
    /// Every contention test needs a holder; racing two real operations would make the tests
    /// depend on scheduling, which is the thing they exist to be independent of.
    /// </summary>
    private static (ManualResetEventSlim Release, Task Holder) HoldGate(LifecycleGate gate)
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var holder = Task.Run(() => gate.Run(
            () =>
            {
                entered.Set();
                release.Wait(EventTimeout);
            },
            LifecycleContention.Abandon));

        Assert.True(entered.Wait(EventTimeout), "the holder never entered the gate");
        return (release, holder);
    }

    #region Construction

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LifecycleGate(
            null!, () => "Nq1", () => TimeSpan.Zero, () => TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_WithNullDeviceName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LifecycleGate(
            NullLogger.Instance, null!, () => TimeSpan.Zero, () => TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_WithNullConnectTimeout_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LifecycleGate(
            NullLogger.Instance, () => "Nq1", null!, () => TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_WithNullTeardownTimeout_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LifecycleGate(
            NullLogger.Instance, () => "Nq1", () => TimeSpan.Zero, null!));
    }

    #endregion

    #region Uncontended

    [Fact]
    public void Run_WhenUncontended_RunsTheOperationAndReportsThatItRan()
    {
        var ran = false;

        var didRun = Create().Run(() => ran = true, LifecycleContention.Fail);

        Assert.True(didRun);
        Assert.True(ran);
    }

    [Fact]
    public async Task RunAsync_WhenUncontended_RunsTheOperationAndReportsThatItRan()
    {
        var ran = false;

        var didRun = await Create().RunAsync(
            () => { ran = true; return Task.CompletedTask; },
            LifecycleContention.Fail,
            CancellationToken.None);

        Assert.True(didRun);
        Assert.True(ran);
    }

    [Fact]
    public void Run_AfterAPreviousOperationCompleted_CanAcquireAgain()
    {
        // The gate has one permit, so a release that did not happen would show up as the second
        // call blocking until its timeout rather than running.
        var gate = Create();
        gate.Run(() => { }, LifecycleContention.Fail);

        var didRun = gate.Run(() => { }, LifecycleContention.Fail);

        Assert.True(didRun);
    }

    #endregion

    #region Contention policy

    [Fact]
    public async Task Run_WhenContendedWithFail_ThrowsWithoutRunningTheOperation()
    {
        // Running alongside is what the gate exists to prevent: two threads would both find no
        // message consumer, both start one, and leave two readers on one stream.
        var gate = Create(name: "Contended Nq1");
        var (release, holder) = HoldGate(gate);
        var ran = false;

        try
        {
            var thrown = Assert.Throws<TimeoutException>(
                () => gate.Run(() => ran = true, LifecycleContention.Fail));

            Assert.Contains("Contended Nq1", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was opened", thrown.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(ran, "the operation ran alongside the holder");
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task RunAsync_WhenContendedWithFail_ThrowsWithoutRunningTheOperation()
    {
        var gate = Create();
        var (release, holder) = HoldGate(gate);
        var ran = false;

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => gate.RunAsync(
                () => { ran = true; return Task.CompletedTask; },
                LifecycleContention.Fail,
                CancellationToken.None));

            Assert.False(ran);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task Run_WhenContendedWithAbandon_ReportsItDidNotRunRatherThanThrowing()
    {
        // Teardown is the resource-release path and Dispose depends on it, so contention must not
        // turn it into an exception — and it must not run alongside either.
        var gate = Create();
        var (release, holder) = HoldGate(gate);
        var ran = false;

        try
        {
            var didRun = gate.Run(() => ran = true, LifecycleContention.Abandon);

            Assert.False(didRun);
            Assert.False(ran);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task RunAsync_WhenContendedWithAbandon_ReportsItDidNotRunRatherThanThrowing()
    {
        var gate = Create();
        var (release, holder) = HoldGate(gate);
        var ran = false;

        try
        {
            var didRun = await gate.RunAsync(
                () => { ran = true; return Task.CompletedTask; },
                LifecycleContention.Abandon,
                CancellationToken.None);

            Assert.False(didRun);
            Assert.False(ran);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public void ContentionWait_UsesTheTeardownBudgetForAbandonAndTheConnectBudgetForFail()
    {
        // A shared budget would suit neither caller: a connect that waits the teardown budget
        // stalls, and a teardown that waits the connect budget gives up on a holder that was
        // about to finish.
        var connectReads = 0;
        var teardownReads = 0;
        var gate = new LifecycleGate(
            NullLogger.Instance,
            () => "Nq1",
            () => { connectReads++; return TimeSpan.FromMilliseconds(50); },
            () => { teardownReads++; return TimeSpan.FromMilliseconds(50); });

        gate.Run(() => { }, LifecycleContention.Fail);
        Assert.Equal(1, connectReads);
        Assert.Equal(0, teardownReads);

        gate.Run(() => { }, LifecycleContention.Abandon);
        Assert.Equal(1, connectReads);
        Assert.Equal(1, teardownReads);
    }

    [Fact]
    public async Task ContentionWait_ReadsTheTimeoutWhenContentionHappens_NotAtConstruction()
    {
        // This is why the timeouts arrive as delegates rather than values. They are virtual on the
        // device so a test can shorten them, and a test subclass assigns its override through an
        // init property that runs AFTER the base constructor builds the gate — so a gate that
        // captured them at construction would silently ignore every override.
        var connectTimeout = TimeSpan.FromMinutes(10);
        var gate = new LifecycleGate(
            NullLogger.Instance,
            () => "Nq1",
            () => connectTimeout,
            () => TimeSpan.FromMinutes(10));

        // Shortened after construction, exactly as an init-set override is.
        connectTimeout = TimeSpan.FromMilliseconds(50);

        var (release, holder) = HoldGate(gate);
        try
        {
            // Would block for ten minutes if the construction-time value were the one in force.
            var contended = Task.Run(() => gate.Run(() => { }, LifecycleContention.Fail));

            Assert.Same(
                contended,
                await Task.WhenAny(contended, Task.Delay(EventTimeout)));
            await Assert.ThrowsAsync<TimeoutException>(() => contended);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    #endregion

    #region Re-entry

    [Fact]
    public void Run_WhenReenteredFromInsideTheCriticalSection_ProceedsInsteadOfDeadlocking()
    {
        // Both connect and disconnect raise StatusChanged from inside their critical section, and
        // a consumer handler calling Disconnect from there is re-entry on the same flow. It ran
        // nested before the gate existed and has to keep working against a non-reentrant semaphore.
        var gate = Create();
        var innerRan = false;

        var didRun = gate.Run(
            () => Assert.True(gate.Run(() => innerRan = true, LifecycleContention.Abandon)),
            LifecycleContention.Fail);

        Assert.True(didRun);
        Assert.True(innerRan);
    }

    [Fact]
    public async Task RunAsync_WhenReenteredAcrossAnAwait_ProceedsInsteadOfDeadlocking()
    {
        // AsyncLocal rather than a thread id precisely so the re-entry flag survives a
        // continuation resuming on a different thread.
        var gate = Create();
        var innerRan = false;

        var didRun = await gate.RunAsync(
            async () =>
            {
                await Task.Yield();
                Assert.True(await gate.RunAsync(
                    () => { innerRan = true; return Task.CompletedTask; },
                    LifecycleContention.Abandon,
                    CancellationToken.None));
            },
            LifecycleContention.Fail,
            CancellationToken.None);

        Assert.True(didRun);
        Assert.True(innerRan);
    }

    [Fact]
    public async Task Run_AfterAReentrantOperationCompletes_TreatsTheNextCallAsContendedAgain()
    {
        // The re-entry flag has to be cleared on the way out. If it leaked, a later caller on a
        // flow that had once been inside the gate would sail past a holder — the double-open the
        // gate exists to prevent, and invisible to a test that only checks the nested call works.
        var gate = Create();
        gate.Run(() => gate.Run(() => { }, LifecycleContention.Abandon), LifecycleContention.Fail);

        var (release, holder) = HoldGate(gate);
        var ran = false;
        try
        {
            Assert.False(gate.Run(() => ran = true, LifecycleContention.Abandon));
            Assert.False(ran);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task RunAsync_ForTeardownWithAnAlreadyCancelledToken_StillRunsTheTeardown()
    {
        // Regression guard for the #341 contract. SemaphoreSlim.WaitAsync throws for an
        // already-cancelled token even when the semaphore is free, so passing a teardown's token
        // to the acquire made EVERY cancelled disconnect skip teardown — reporting Disconnected
        // with the transport still open and the message pumps still running — not just a contended
        // one. The token means "shorten the courtesy wait for an in-flight exchange", never
        // "abandon the disconnect".
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        var didRun = await Create().RunAsync(
            () => { ran = true; return Task.CompletedTask; },
            LifecycleContention.Abandon,
            cts.Token);

        Assert.True(didRun);
        Assert.True(ran);
    }

    [Fact]
    public async Task RunAsync_ForConnectWithAnAlreadyCancelledToken_HonoursTheToken()
    {
        // The connect path is the opposite case: ConnectAsync is documented to be abandonable and
        // to surface an OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().RunAsync(
            () => { ran = true; return Task.CompletedTask; },
            LifecycleContention.Fail,
            cts.Token));

        Assert.False(ran);
    }

    #endregion

    #region Release

    [Fact]
    public void Run_WhenTheOperationThrows_StillReleasesTheGate()
    {
        // A connect that fails must not leave the gate held: every later connect and — worse —
        // every later teardown would then be permanently contended.
        var gate = Create();

        Assert.Throws<InvalidOperationException>(() => gate.Run(
            () => throw new InvalidOperationException("connect-boom"),
            LifecycleContention.Fail));

        Assert.True(gate.Run(() => { }, LifecycleContention.Fail));
    }

    [Fact]
    public async Task RunAsync_WhenTheOperationThrows_StillReleasesTheGate()
    {
        var gate = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(
            () => throw new InvalidOperationException("connect-boom"),
            LifecycleContention.Fail,
            CancellationToken.None));

        Assert.True(await gate.RunAsync(
            () => Task.CompletedTask, LifecycleContention.Fail, CancellationToken.None));
    }

    #endregion

    #region Logger isolation

    [Fact]
    public async Task Run_WhenContendedAndTheLoggerThrows_StillReportsTheContention()
    {
        // A consumer-supplied logger must never affect device operation, least of all on the path
        // that is already reporting trouble.
        var gate = Create(logger: new ThrowingLogger());
        var (release, holder) = HoldGate(gate);

        try
        {
            Assert.Throws<TimeoutException>(() => gate.Run(() => { }, LifecycleContention.Fail));
            Assert.False(gate.Run(() => { }, LifecycleContention.Abandon));
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger-boom");
    }

    #endregion
}
