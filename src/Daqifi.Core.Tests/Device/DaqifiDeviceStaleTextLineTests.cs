using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for the stale-line boundary in the text exchange (raised while fixing #396).
/// </summary>
/// <remarks>
/// A late reply to an EARLIER command can still be in flight when the next text exchange opens
/// its consumer, and would otherwise be returned as part of the new exchange's response. That is
/// wrong for every caller, but it is actively dangerous for one that infers device liveness from
/// response content: the SD listing accepts a <c>SYSTem:ERRor?</c> reply as proof that the device
/// answered and that the listing before it is complete. A stale line satisfying that check would
/// let a silent device pass as a healthy empty SD card — the exact bug #396 is about.
/// </remarks>
public class DaqifiDeviceStaleTextLineTests
{
    [Fact]
    public async Task ExecuteTextCommand_DropsLinesThatArrivedBeforeTheExchangeSentAnything()
    {
        // The stale line is released into the stream at the moment the exchange binds its text
        // consumer — after the protobuf consumer has been stopped, and before the setup action
        // has sent anything. That is exactly the window a late reply to an earlier command can
        // land in. The device then stays silent, as one that has stopped answering would.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Preloaded Device", transport);

        device.Connect();
        transport.ReleaseOnStreamAccess(2); // 2nd access inside the exchange = text-consumer bind

        var lines = await device.CallExecuteTextCommandAsync(() => { });

        // The exchange sent nothing, so nothing in it can legitimately have been answered.
        Assert.Empty(lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_KeepsLinesThatArriveAfterTheExchangeSentSomething()
    {
        // The complement, so the fix cannot be "drop everything": a reply that arrives once the
        // setup action has sent its command must still be returned.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Answering Device", transport);

        device.Connect();

        var lines = await device.CallExecuteTextCommandAsync(() => transport.Release());

        Assert.Contains(lines, l => l.Contains("No error"));

        device.Disconnect();
    }

    // ── The capture-to-send window (#553) — the gap between the stale-line boundary capture
    // and the setup action actually finishing, which the access-counted mock above cannot
    // target: both existing Stream accesses land before the boundary is even captured. These
    // use a dedicated hook fired at the boundary itself, and a setup action slow enough that a
    // line released there is guaranteed to have arrived before the boundary closes. ───────────

    [Fact]
    public async Task ExecuteTextCommand_DropsABlankThatArrivesWhileTheCommandIsStillBeingSent()
    {
        // The blank is released the instant the boundary is captured — squarely inside the
        // window a stale reply to an earlier command can land in, and before this exchange has
        // actually gotten its command onto the wire. Without the fix this leaks through as
        // "the device answered and its response was empty", which is the exact SYSTem:LOG?
        // misattribution the issue describes.
        using var transport = new ReleaseOnStreamAccessMockTransport("\r\n");
        using var device = new StaleBoundaryHookTestableDevice(
            "Slow-Sending Device", transport, onStaleLineBoundaryCaptured: () => transport.Release());

        device.Connect();

        var lines = await device.CallExecuteTextCommandAsync(
            () => Thread.Sleep(100),
            keepBlankLines: true);

        Assert.Empty(lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_KeepsAContentLineThatArrivesWhileTheCommandIsStillBeingSent()
    {
        // The complement, and the reason the fix only narrows the boundary for blanks: a content
        // line released in the same window is left alone, matching the deliberate decision
        // recorded in #553 not to risk discarding a genuinely fast reply.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleBoundaryHookTestableDevice(
            "Fast-Replying Device", transport, onStaleLineBoundaryCaptured: () => transport.Release());

        device.Connect();

        var lines = await device.CallExecuteTextCommandAsync(() => Thread.Sleep(100));

        Assert.Contains(lines, l => l.Contains("No error"));

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_DoesNotStallWhenTheReplyArrivesBeforeTheWaitLoopStarts()
    {
        // Regression for #592: the wait loop's hasReceivedAny flag only flipped on a line-count
        // increase OBSERVED INSIDE the loop, so a reply already sitting in collectedLines by the
        // time the loop took its first sample was invisible to it -- the exchange then sat out
        // the full responseTimeoutMs instead of the short completionTimeoutMs, even though
        // nothing was actually missing. The content line is released at the stale-line boundary
        // (before the setup action even runs), and the setup action then sleeps well past any
        // reasonable read-thread latency, so the reply is guaranteed to already be in
        // collectedLines by the time the wait loop takes its first sample -- deterministically
        // reproducing the "reply beat the loop" case a fast device produces on real hardware.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleBoundaryHookTestableDevice(
            "Already-Answered Device", transport, onStaleLineBoundaryCaptured: () => transport.Release());

        device.Connect();

        var sw = Stopwatch.StartNew();
        var lines = await device.CallExecuteTextCommandAsync(
            () => Thread.Sleep(100),
            responseTimeoutMs: 3000,
            completionTimeoutMs: 100);
        sw.Stop();

        Assert.Contains(lines, l => l.Contains("No error"));
        // Well short of responseTimeoutMs (3000ms): without the fix this reliably took >=3000ms,
        // because hasReceivedAny never flipped and phase 1's full timeout ran out.
        Assert.True(
            sw.ElapsedMilliseconds < 2000,
            $"Expected the exchange to finish near completionTimeoutMs (100ms), took {sw.ElapsedMilliseconds}ms.");

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_DoesNotExitEarlyWhenOnlyAStaleBlankPrecedesTheRealResponse()
    {
        // Regression: hasReceivedAny must not be seeded true by a blank that arrived in the
        // capture-to-send window (issue #553) and will be discarded as stale by the projection
        // below (index < sentBoundaryLineCount). A raw line-count comparison against
        // staleLineCount cannot tell that blank apart from real evidence, and would flip the wait
        // loop into phase 2's short completionTimeoutMs immediately -- ending collection before
        // the real response (released deliberately late here) has a chance to arrive, and
        // returning empty for a device that did, in fact, answer.
        using var transport = new TwoStageReleaseTransport(staleLine: "\r\n", contentLine: "0,\"No error\"\r\n");
        using var device = new StaleBoundaryHookTestableDevice(
            "Stale-Then-Real Device", transport, onStaleLineBoundaryCaptured: () => transport.ReleaseStale());

        device.Connect();

        var lines = await device.CallExecuteTextCommandAsync(
            () =>
            {
                // Give the reader thread time to actually collect the stale blank released by the
                // boundary hook above before the command is considered sent, then release the real
                // response after this setup action has already returned -- squarely inside the
                // wait loop's window, and after phase 2's short timeout would have already fired
                // on the bug's seed.
                Thread.Sleep(50);
                _ = Task.Delay(150).ContinueWith(_ => transport.ReleaseContent());
            },
            responseTimeoutMs: 1000,
            completionTimeoutMs: 100);

        Assert.Contains(lines, l => l.Contains("No error"));

        device.Disconnect();
    }

    // ── The queue-to-write window (#593) — what is left of #553 once #591 has run. The setup
    // action returns when the command has been handed to the producer, not when the producer has
    // written it, so a blank can still arrive after the exchange believes it has sent and before
    // the device has been asked anything. The boundary #591 captures cannot be moved later to
    // cover it (that is the drain, and it breaks SYSTem:LOG? outright); each line carries the
    // producer's started-write count instead. The producer is parked inside a blocking write here
    // to hold the window open, which on real hardware is sub-millisecond. ─────────────────────

    [Fact]
    public async Task ExecuteTextCommand_DropsABlankThatArrivesAfterTheCommandIsQueuedButBeforeItIsWritten()
    {
        // An earlier command holds the producer thread, so the command this exchange queues
        // cannot reach the wire at all while the blank arrives. Nothing has been asked of the
        // device, so a blank — which only ever terminates a dump — cannot be answering: without
        // the fix it is returned as "the device answered, and its log is empty".
        using var transport = new QueuedWriteTransport();
        using var device = new StaleLineTestableDevice("Queued-Command Device", transport);

        device.Connect();

        transport.HoldWrites();
        device.Send(new ScpiMessage("EARLIER:COMMAND"));
        Assert.True(
            transport.WaitForWriteStarted(TimeSpan.FromSeconds(10)),
            "The producer never picked up the earlier command.");

        var lines = await device.CallExecuteTextCommandAsync(
            () =>
            {
                device.Send(new ScpiMessage("SYSTem:LOG?"));

                // Released only after this setup action has returned, so the line-count boundary
                // from #591 cannot be what drops it: by then the exchange believes it has sent,
                // and only the write count knows the command is still sitting in the queue.
                _ = Task.Delay(75).ContinueWith(_ => transport.ReleaseLine("\r\n"));
            },
            keepBlankLines: true);

        Assert.Empty(lines);

        transport.ReleaseWrite();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_KeepsABlankThatArrivesOnceTheCommandIsBeingWritten()
    {
        // The guard #593 asks for by name. SYSTem:LOG? terminates its dump with a blank line, and
        // on a bench Nq1 that blank comes back about 6ms after the write starts — while the write
        // may well still be in progress. Anything that writes such a blank off as a leftover
        // reports a healthy device as silent, which is exactly what draining before the boundary
        // did, 10/10 runs. The write is deliberately still open when the blank lands here, so a
        // marker keyed off the write FINISHING rather than starting would fail this test.
        using var transport = new QueuedWriteTransport();
        using var device = new StaleLineTestableDevice("Answering Log Device", transport);

        device.Connect();

        transport.HoldWrites();

        var lines = await device.CallExecuteTextCommandAsync(
            () =>
            {
                device.Send(new ScpiMessage("SYSTem:LOG?"));
                Assert.True(
                    transport.WaitForWriteStarted(TimeSpan.FromSeconds(10)),
                    "The producer never started the command's write.");

                _ = Task.Delay(75).ContinueWith(_ => transport.ReleaseLine("\r\n"));
            },
            keepBlankLines: true);

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Equal(string.Empty, line));

        transport.ReleaseWrite();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_DoesNotExitEarlyWhenAQueuedCommandsBlankPrecedesTheRealResponse()
    {
        // The wait loop has to agree with the projection about what counts as an answer. A blank
        // that arrives while the command is still queued is discarded later, so treating it as
        // "the device has started replying" flips the loop into its short completion timeout and
        // ends collection before the real response arrives — returning empty for a device that
        // did answer, which is the failure this whole boundary exists to prevent.
        using var transport = new QueuedWriteTransport();
        using var device = new StaleLineTestableDevice("Late-Answering Device", transport);

        device.Connect();

        transport.HoldWrites();
        device.Send(new ScpiMessage("EARLIER:COMMAND"));
        Assert.True(
            transport.WaitForWriteStarted(TimeSpan.FromSeconds(10)),
            "The producer never picked up the earlier command.");

        var lines = await device.CallExecuteTextCommandAsync(
            () =>
            {
                device.Send(new ScpiMessage("SYSTem:ERRor?"));
                _ = Task.Delay(75).ContinueWith(_ => transport.ReleaseLine("\r\n"));
                _ = Task.Delay(500).ContinueWith(_ => transport.ReleaseLine("0,\"No error\"\r\n"));
            },
            responseTimeoutMs: 3000,
            completionTimeoutMs: 150);

        // Without the fix the loop breaks around 225ms — well before the real reply at 500ms.
        Assert.Contains(lines, l => l.Contains("No error"));

        transport.ReleaseWrite();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithPrepare_RunsPrepareBeforeTheSetupAction()
    {
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Prepared Device", transport);

        device.Connect();

        var order = new List<string>();
        await device.CallWithPrepareAsync(
            _ => { order.Add("prepare"); return Task.CompletedTask; },
            () => order.Add("setup"));

        Assert.Equal(new[] { "prepare", "setup" }, order);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithPrepare_RunsPrepareInsideTheExchange()
    {
        // The property that matters for the SD card operations: the prepare phase holds the
        // device-wide text-exchange lock, so no competing exchange can interleave between the SPI
        // bus switch it performs and the commands that depend on it. Asserted through the
        // exchange's own re-entrancy guard rather than by racing two threads — if prepare runs
        // inside the critical section, a nested exchange must be refused, and if it had been
        // hoisted back outside the lock this would silently succeed instead.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Nested Device", transport);

        device.Connect();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.CallWithPrepareAsync(
                async _ => await device.CallExecuteTextCommandAsync(() => { }),
                () => { }));

        Assert.Contains("not re-entrant", ex.Message);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_CarryingAPreparePhase_IsStillCaughtByASubclassOverride()
    {
        // The prepare phase is a parameter on the existing virtual rather than a second virtual
        // method, so a subclass that overrides ExecuteTextCommandAsync keeps intercepting the SD
        // operations that use it. A parallel seam would route past such an override with no compile
        // error and no runtime signal — an instrumented device or test double would simply stop
        // seeing SD traffic. If this ever regresses to a sibling method, this test fails.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new InterceptingTestableDevice("Intercepting Device", transport);

        device.Connect();

        var prepared = false;
        var lines = await device.CallWithPrepareAsync(
            _ => { prepared = true; return Task.CompletedTask; },
            () => { });

        Assert.True(device.Intercepted, "The subclass override did not see the call.");
        Assert.True(prepared, "The override was handed the prepare phase and ran it.");
        Assert.Equal(new[] { "from the override" }, lines);

        device.Disconnect();
    }

    // ── Finalize phase (#407) — the mirror of the prepare phase above. An exchange that
    // switches shared device state on the way in has to switch it back before anything else
    // runs, or only half the pairing is serialized. ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_RunsFinalizeAfterTheSetupAction()
    {
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Finalized Device", transport);

        device.Connect();

        var order = new List<string>();
        await device.CallWithFinalizeAsync(
            () => order.Add("setup"),
            () => { order.Add("finalize"); return Task.CompletedTask; },
            prepareAsync: _ => { order.Add("prepare"); return Task.CompletedTask; });

        Assert.Equal(new[] { "prepare", "setup", "finalize" }, order);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_RunsFinalizeInsideTheExchange()
    {
        // The property #407 is about: the finalize phase holds the same lock acquisition the
        // prepare phase does, so nothing can run between this exchange's commands and the state
        // it restores. Asserted through the exchange's own re-entrancy guard rather than by
        // racing threads — a nested exchange started from the finalize must be refused, and if
        // the restore were back outside the lock this would quietly succeed instead.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Nested Finalize Device", transport);

        device.Connect();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.CallWithFinalizeAsync(
                () => { },
                async () => await device.CallExecuteTextCommandAsync(() => { })));

        Assert.Contains("not re-entrant", ex.Message);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_RunsFinalizeWhenTheExchangeThrows()
    {
        // The reason the finalize is a phase the exchange owns rather than "another prepare at
        // the end": a failed exchange is exactly when the device most needs putting back.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Failing Device", transport);

        device.Connect();

        var finalized = false;

        var ex = await Assert.ThrowsAsync<InvalidTimeZoneException>(
            () => device.CallWithFinalizeAsync(
                () => throw new InvalidTimeZoneException("the exchange failed"),
                () => { finalized = true; return Task.CompletedTask; }));

        Assert.Equal("the exchange failed", ex.Message);
        Assert.True(finalized, "The finalize phase did not run for a failed exchange.");

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_WhenBothFail_SurfacesTheExchangeFailure()
    {
        // A cleanup failure must never hide the failure that caused the cleanup: the caller
        // needs the original to diagnose anything at all.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Doubly Failing Device", transport);

        device.Connect();

        var ex = await Assert.ThrowsAsync<InvalidTimeZoneException>(
            () => device.CallWithFinalizeAsync(
                () => throw new InvalidTimeZoneException("the exchange failed"),
                () => throw new NotSupportedException("the restore failed too")));

        Assert.Equal("the exchange failed", ex.Message);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_WhenOnlyTheFinalizeFails_SurfacesThatFailure()
    {
        // The complement, so "never throw from the finalize" isn't the rule: with nothing else
        // unwinding, a failed restore is the only failure there is, and reporting success would
        // hand the caller a device left in the prepared state.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Failing Restore Device", transport);

        device.Connect();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => device.CallWithFinalizeAsync(
                () => { },
                () => throw new NotSupportedException("the restore failed")));

        Assert.Equal("the restore failed", ex.Message);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_WhenTheFinalizeFails_TheExchangeLockIsStillReleased()
    {
        // The finalize runs from the exchange's own finally, so a failure raised straight out of
        // it would abandon the rest of that finally — the lock included — and every later exchange
        // on the device would hang forever. Both outcomes are checked because they take different
        // routes out: the restore failing alone, and the restore failing on top of a failed
        // exchange.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Leaky Restore Device", transport);

        device.Connect();

        var restoreFailed = device.CallWithFinalizeAsync(
            () => { },
            () => throw new NotSupportedException("the restore failed"));
        await AssertCompletesAsync(restoreFailed);
        await Assert.ThrowsAsync<NotSupportedException>(() => restoreFailed);

        var bothFailed = device.CallWithFinalizeAsync(
            () => throw new InvalidTimeZoneException("the exchange failed"),
            () => throw new NotSupportedException("the restore failed too"));
        await AssertCompletesAsync(bothFailed);
        await Assert.ThrowsAsync<InvalidTimeZoneException>(() => bothFailed);

        var next = device.CallExecuteTextCommandAsync(() => { });
        await AssertCompletesAsync(next);
        await next;

        device.Disconnect();
    }

    /// <summary>
    /// Waits for a call with a bound, so a leaked exchange lock fails the test that is looking for
    /// it instead of hanging the whole run.
    /// </summary>
    private static async Task AssertCompletesAsync(Task call)
    {
        var winner = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(call, winner);
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_NoOtherExchangeRunsBeforeTheFinalize()
    {
        // The race from #407 stated directly: a competing exchange must not be able to run
        // between one exchange's commands and its restore. The second call is launched as soon
        // as the first has sent, and the first's finalize then dawdles — plenty of room for the
        // second to slip in if the restore were outside the lock.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Serialized Device", transport);

        device.Connect();

        var order = new List<string>();
        var gate = new object();
        void Record(string step)
        {
            lock (gate)
            {
                order.Add(step);
            }
        }

        using var firstHasSent = new ManualResetEventSlim(false);

        // The finalize dawdles on purpose. Recording a start and an end around the wait is what
        // makes this a regression detector rather than a coincidence: with the restore outside
        // the lock, the second exchange acquires it the moment the first exchange returns and its
        // setup lands INSIDE that window.
        var first = device.CallWithFinalizeAsync(
            () => { Record("first.setup"); firstHasSent.Set(); },
            async () =>
            {
                Record("first.finalize.start");
                await Task.Delay(300);
                Record("first.finalize.end");
            });

        Assert.True(firstHasSent.Wait(TimeSpan.FromSeconds(10)), "The first exchange never sent.");

        var second = Task.Run(() => device.CallExecuteTextCommandAsync(() => Record("second.setup")));

        await Task.WhenAll(first, second);

        lock (gate)
        {
            Assert.Equal(
                new[] { "first.setup", "first.finalize.start", "first.finalize.end", "second.setup" },
                order);
        }

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommandWithFinalize_WhenValidationRefusesTheExchange_DoesNotRunFinalize()
    {
        // The one case the finalize is skipped: the exchange never got past validation, so it
        // never touched the device and there is nothing to put back. Running it here would only
        // add a second failure on a device that is already gone.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Unconnected Device", transport);

        var finalized = false;

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.CallWithFinalizeAsync(
                () => { },
                () => { finalized = true; return Task.CompletedTask; }));

        Assert.False(finalized, "The finalize phase ran for an exchange that never started.");
    }

    /// <summary>
    /// Exposes <see cref="DaqifiDevice.OnStaleLineBoundaryCaptured"/> so a test can release a line
    /// into the transport at the exact instant the exchange's stale-line boundary is captured —
    /// the capture-to-send window from issue #553.
    /// </summary>
    private sealed class StaleBoundaryHookTestableDevice : DaqifiDevice
    {
        private readonly Action _onStaleLineBoundaryCaptured;

        public StaleBoundaryHookTestableDevice(
            string name, IStreamTransport transport, Action onStaleLineBoundaryCaptured)
            : base(name, transport)
        {
            _onStaleLineBoundaryCaptured = onStaleLineBoundaryCaptured;
        }

        internal override void OnStaleLineBoundaryCaptured() => _onStaleLineBoundaryCaptured();

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(
            Action setupAction,
            bool keepBlankLines = false,
            int responseTimeoutMs = 500,
            int completionTimeoutMs = 150)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: responseTimeoutMs,
                completionTimeoutMs: completionTimeoutMs,
                keepBlankLines: keepBlankLines);
        }
    }

    /// <summary>
    /// Stands in for a downstream subclass or test double that intercepts the text exchange —
    /// the case the single-seam design protects.
    /// </summary>
    private sealed class InterceptingTestableDevice : StaleLineTestableDevice
    {
        public InterceptingTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public bool Intercepted { get; private set; }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            try
            {
                Intercepted = true;

                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                setupAction();
                return new List<string> { "from the override" };
            }
            finally
            {
                // Honor the exchange's finalize phase the way the real device does: it runs
                // however the exchange ended, still inside the exchange (#407).
                if (finalizeAsync != null)
                {
                    await finalizeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Exposes the protected text-exchange entry points.</summary>
    private class StaleLineTestableDevice : DaqifiDevice
    {
        public StaleLineTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(
            Action setupAction,
            bool keepBlankLines = false,
            int responseTimeoutMs = 500,
            int completionTimeoutMs = 150)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: responseTimeoutMs,
                completionTimeoutMs: completionTimeoutMs,
                keepBlankLines: keepBlankLines);
        }

        public Task<IReadOnlyList<string>> CallWithPrepareAsync(
            Func<CancellationToken, Task> prepareAsync,
            Action setupAction)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: 500,
                completionTimeoutMs: 150,
                prepareAsync: prepareAsync);
        }

        public Task<IReadOnlyList<string>> CallWithFinalizeAsync(
            Action setupAction,
            Func<Task> finalizeAsync,
            Func<CancellationToken, Task>? prepareAsync = null)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: 500,
                completionTimeoutMs: 150,
                prepareAsync: prepareAsync,
                finalizeAsync: finalizeAsync);
        }
    }

    /// <summary>
    /// Transport whose stream withholds one canned line until released, and which can arm that
    /// release on the Nth access of its <see cref="Stream"/> property.
    /// </summary>
    /// <remarks>
    /// Keying off the property access — rather than a delay — makes the timing deterministic:
    /// the text exchange reads <c>Stream</c> once up front and again when it binds the temporary
    /// text consumer, and that second access happens after the protobuf consumer has been stopped
    /// (so it cannot swallow the line first) and before the setup action runs.
    /// </remarks>
    private sealed class ReleaseOnStreamAccessMockTransport : IStreamTransport
    {
        private readonly WithheldLineStream _stream;
        private int _streamAccessCount;
        private int _releaseOnAccess = -1;
        private bool _isConnected;
        private bool _disposed;

        public ReleaseOnStreamAccessMockTransport(string line)
        {
            _stream = new WithheldLineStream(line);
        }

        public Stream Stream
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ReleaseOnStreamAccessMockTransport));

                var access = Interlocked.Increment(ref _streamAccessCount);
                if (_releaseOnAccess > 0 && access == _releaseOnAccess)
                {
                    _stream.Release();
                }

                return _stream;
            }
        }

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Withheld: Connected" : "Withheld: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Arms the release for the Nth subsequent access of <see cref="Stream"/>.</summary>
        public void ReleaseOnStreamAccess(int accessNumber)
        {
            Interlocked.Exchange(ref _streamAccessCount, 0);
            _releaseOnAccess = accessNumber;
        }

        /// <summary>Releases the withheld line immediately.</summary>
        public void Release() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReleaseOnStreamAccessMockTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _disposed = true;
        }

        private sealed class WithheldLineStream : Stream
        {
            private readonly byte[] _line;
            private readonly object _gate = new();
            private bool _released;
            private int _position;

            public WithheldLineStream(string line) => _line = Encoding.ASCII.GetBytes(line);

            public void Release()
            {
                lock (_gate)
                {
                    _released = true;
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_released && _position < _line.Length)
                    {
                        var toCopy = Math.Min(count, _line.Length - _position);
                        Array.Copy(_line, _position, buffer, offset, toCopy);
                        _position += toCopy;
                        return toCopy;
                    }
                }

                // Idle link: nothing to hand over, and no busy-spin in the reader thread.
                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    /// <summary>
    /// Transport whose stream withholds two canned lines, released independently -- a "stale"
    /// line and a "content" line -- so a test can put one in flight before the other without
    /// either racing the reader thread against the other's release.
    /// </summary>
    private sealed class TwoStageReleaseTransport : IStreamTransport
    {
        private readonly TwoStageStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public TwoStageReleaseTransport(string staleLine, string contentLine)
        {
            _stream = new TwoStageStream(staleLine, contentLine);
        }

        public Stream Stream
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TwoStageReleaseTransport));
                return _stream;
            }
        }

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "TwoStage: Connected" : "TwoStage: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Releases the stale line into the stream immediately.</summary>
        public void ReleaseStale() => _stream.ReleaseStale();

        /// <summary>Releases the content line into the stream immediately.</summary>
        public void ReleaseContent() => _stream.ReleaseContent();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TwoStageReleaseTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _disposed = true;
        }

        private sealed class TwoStageStream : Stream
        {
            private readonly byte[] _stale;
            private readonly byte[] _content;
            private readonly object _gate = new();
            private bool _staleReleased;
            private bool _contentReleased;
            private int _stalePosition;
            private int _contentPosition;

            public TwoStageStream(string staleLine, string contentLine)
            {
                _stale = Encoding.ASCII.GetBytes(staleLine);
                _content = Encoding.ASCII.GetBytes(contentLine);
            }

            public void ReleaseStale()
            {
                lock (_gate)
                {
                    _staleReleased = true;
                }
            }

            public void ReleaseContent()
            {
                lock (_gate)
                {
                    _contentReleased = true;
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    // The stale line always drains first -- it is the one released earliest on
                    // real hardware too (a leftover from an earlier exchange, arriving before this
                    // exchange has sent anything).
                    if (_staleReleased && _stalePosition < _stale.Length)
                    {
                        var toCopy = Math.Min(count, _stale.Length - _stalePosition);
                        Array.Copy(_stale, _stalePosition, buffer, offset, toCopy);
                        _stalePosition += toCopy;
                        return toCopy;
                    }

                    if (_contentReleased && _contentPosition < _content.Length)
                    {
                        var toCopy = Math.Min(count, _content.Length - _contentPosition);
                        Array.Copy(_content, _contentPosition, buffer, offset, toCopy);
                        _contentPosition += toCopy;
                        return toCopy;
                    }
                }

                // Idle link: nothing to hand over, and no busy-spin in the reader thread.
                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    /// <summary>
    /// Transport whose stream can be told to park inside <see cref="Stream.Write"/>, and which
    /// hands canned lines to the reader on demand.
    /// </summary>
    /// <remarks>
    /// Holding the write open is what makes the queue-to-write window of issue #593 targetable:
    /// while the producer thread sits in a write, everything a caller queues behind it stays
    /// queued, so a test can put a line on the wire at a moment when the device provably has not
    /// been asked anything. On real hardware that window is sub-millisecond and no delay-based
    /// double can land in it. Writes pass straight through until <see cref="HoldWrites"/> is
    /// called, so connecting the device is unaffected.
    /// </remarks>
    private sealed class QueuedWriteTransport : IStreamTransport
    {
        private readonly HoldableStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(QueuedWriteTransport));
                return _stream;
            }
        }

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Queued: Connected" : "Queued: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Makes every subsequent write park until <see cref="ReleaseWrite"/>.</summary>
        public void HoldWrites() => _stream.HoldWrites();

        /// <summary>Waits for the producer thread to actually enter a held write.</summary>
        public bool WaitForWriteStarted(TimeSpan timeout) => _stream.WaitForWriteStarted(timeout);

        /// <summary>Lets the held write — and every write after it — through.</summary>
        public void ReleaseWrite() => _stream.ReleaseWrite();

        /// <summary>Puts a line on the wire for the reader to pick up.</summary>
        public void ReleaseLine(string line) => _stream.ReleaseLine(line);

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(QueuedWriteTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _disposed = true;

            // A test that failed before releasing must not leave the producer thread parked.
            _stream.ReleaseWrite();
        }

        private sealed class HoldableStream : Stream
        {
            private readonly object _gate = new();
            private readonly Queue<byte[]> _pending = new();
            private readonly ManualResetEventSlim _writeStarted = new(false);
            private readonly ManualResetEventSlim _writeRelease = new(true);
            private byte[]? _current;
            private int _position;
            private volatile bool _holdWrites;

            public void HoldWrites()
            {
                _writeRelease.Reset();
                _holdWrites = true;
            }

            public bool WaitForWriteStarted(TimeSpan timeout) => _writeStarted.Wait(timeout);

            public void ReleaseWrite()
            {
                _holdWrites = false;
                _writeRelease.Set();
            }

            public void ReleaseLine(string line)
            {
                lock (_gate)
                {
                    _pending.Enqueue(Encoding.ASCII.GetBytes(line));
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_current == null || _position >= _current.Length)
                    {
                        _current = _pending.Count > 0 ? _pending.Dequeue() : null;
                        _position = 0;
                    }

                    if (_current != null)
                    {
                        var toCopy = Math.Min(count, _current.Length - _position);
                        Array.Copy(_current, _position, buffer, offset, toCopy);
                        _position += toCopy;
                        return toCopy;
                    }
                }

                // Idle link: nothing to hand over, and no busy-spin in the reader thread.
                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_holdWrites)
                {
                    return;
                }

                _writeStarted.Set();

                // Bounded, so a test that never releases fails on its own assertion rather than
                // wedging the producer thread for the rest of the run.
                _writeRelease.Wait(TimeSpan.FromSeconds(30));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _writeRelease.Set();
                }

                base.Dispose(disposing);
            }
        }
    }
}
