using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for per-device operation serialization (#342).
/// </summary>
/// <remarks>
/// The contract under test: individual calls are safe from any thread; a sequence that must not be
/// split goes in <see cref="DaqifiDevice.RunExclusiveAsync{TResult}"/>; a <see cref="DaqifiDevice.Send{T}"/>
/// from another thread is deferred rather than blocked while one runs; and nothing here can
/// deadlock against the text-exchange or lifecycle locks that were already in place.
/// </remarks>
public class DaqifiDeviceOperationSerializationTests
{
    private static readonly TimeSpan DeadlockBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a helper thread waits for the phase of the run it is meant to race. Deliberately
    /// shorter than the joins that follow, so "the phase never happened" fails as itself instead of
    /// as a join timeout.
    /// </summary>
    private static readonly TimeSpan PhaseBoundaryWait = TimeSpan.FromSeconds(5);

    // ── Mutual exclusion ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_NeverRunsTwoOperationsAtOnce()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Exclusive Device", transport);
        device.Connect();

        var overlapped = false;
        var inFlight = 0;

        var operations = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            device.RunExclusiveAsync(async _ =>
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    Volatile.Write(ref overlapped, true);
                }

                await Task.Delay(25);
                Interlocked.Decrement(ref inFlight);
            })));

        await Task.WhenAll(operations).WaitAsync(DeadlockBudget);

        Assert.False(Volatile.Read(ref overlapped), "Two exclusive operations ran at the same time.");

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_ReleasesTheLockWhenTheBodyThrows()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Throwing Device", transport);
        device.Connect();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.RunExclusiveAsync(_ => throw new InvalidOperationException("boom")));

        // A leaked lock would hang here instead of completing.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_WhenDisposed_ThrowsDeviceNotConnected()
    {
        var transport = new RecordingTransport();
        var device = new DaqifiDevice("Disposed Device", transport);
        device.Connect();
        device.Dispose();

        var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.RunExclusiveAsync(_ => Task.CompletedTask));

        Assert.True(ex.IsShuttingDown);
    }

    // ── Reentrancy / deadlock guards ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_IsReentrantOnTheSameFlow()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Reentrant Device", transport);
        device.Connect();

        var reached = false;

        await device.RunExclusiveAsync(async ct =>
        {
            await device.RunExclusiveAsync(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            }, ct);
        }).WaitAsync(DeadlockBudget);

        Assert.True(reached);

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_AllowsANestedTextExchange()
    {
        // The deadlock this guards: text queries (SD listings, diagnostics, the capability
        // document) take the same lock RunExclusiveAsync holds. A non-reentrant acquisition here
        // would hang forever on a lock this very flow is holding.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Nesting Device", transport);
        device.Connect();

        var lines = await device.RunExclusiveAsync(
            _ => device.RunTextExchangeAsync(() => device.Send(ScpiMessageProducer.GetDeviceInfo)))
            .WaitAsync(DeadlockBudget);

        Assert.NotNull(lines);
        Assert.Contains(transport.Writes, w => w.Contains("SYSInfoPB", StringComparison.Ordinal));

        device.Disconnect();
    }

    [Fact]
    public async Task Disconnect_FromInsideAnExclusiveOperation_DoesNotStall()
    {
        // Teardown waits for the operation lock before ripping the transport away. From inside an
        // exclusive operation that is the caller's own lock, so it must run nested instead of
        // burning the whole 10s courtesy budget waiting on itself.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Self-disconnecting Device", transport);
        device.Connect();

        var sw = Stopwatch.StartNew();
        await device.RunExclusiveAsync(_ =>
        {
            device.Disconnect();
            return Task.CompletedTask;
        }).WaitAsync(DeadlockBudget);
        sw.Stop();

        Assert.False(device.IsConnected);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Disconnect from inside an exclusive operation took {sw.Elapsed.TotalSeconds:0.#}s; it waited on its own lock.");
    }

    [Fact]
    public async Task Disconnect_FromAnotherFlow_StillTearsDownWhileAnOperationIsInFlight()
    {
        // Teardown must never be blocked indefinitely by an operation. The cancellation token
        // shortens the courtesy wait, which is the same exit the 10s timeout takes.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Torn-down Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        using var shortWait = new CancellationTokenSource();
        await shortWait.CancelAsync();
        await device.DisconnectAsync(shortWait.Token).WaitAsync(DeadlockBudget);

        Assert.False(device.IsConnected);

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);
    }

    // ── Send deferral ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_FromAnotherFlow_IsHeldBackUntilTheOperationFinishes()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Deferring Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));

        // Long enough that the producer thread would have written it had it been queued.
        await Task.Delay(250);
        Assert.DoesNotContain(transport.Writes, w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "DIO:PORt:STATe");
    }

    [Fact]
    public async Task Send_FromAnotherFlow_DoesNotBlockWhileAnOperationIsInFlight()
    {
        // Deferred, not blocked: Send has always been fire-and-forget and must keep returning
        // immediately even when the device is owned by someone else.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Non-blocking Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        var sw = Stopwatch.StartNew();
        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Send blocked for {sw.Elapsed.TotalSeconds:0.##}s while another flow owned the device.");

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);
    }

    [Fact]
    public async Task Send_FromTheOwningFlow_GoesStraightOut()
    {
        // The operation's own commands must not be parked — the operation would be waiting on
        // itself to finish before its own commands could be sent.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Owning Device", transport);
        device.Connect();

        await device.RunExclusiveAsync(async _ =>
        {
            device.Send(ScpiMessageProducer.SetDioPortState(4, 1));
            await WaitForWriteAsync(transport, "DIO:PORt:STATe");
        }).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_DeferredMessagesAreDeliveredInOrder()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Ordering Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var channel = 1; channel <= 5; channel++)
            {
                device.Send(ScpiMessageProducer.SetDioPortState(channel, 1));
            }
        });

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "DIO:PORt:STATe 5");

        var states = transport.Writes
            .Where(w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, states.Count);
        for (var channel = 1; channel <= 5; channel++)
        {
            Assert.Contains($"STATe {channel}", states[channel - 1], StringComparison.Ordinal);
        }

        device.Disconnect();
    }

    [Fact]
    public async Task Send_FromAnotherFlow_IsHeldBackDuringAPlainTextExchange()
    {
        // The hazard the whole feature exists for: a text query owns the stream with the protobuf
        // consumer stopped, so a command written by another thread has its reply collected as part
        // of that query's answer.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Querying Device", transport);
        device.Connect();

        // The sender is a real thread started before the exchange opens, so it carries none of the
        // exchange's execution context. That matters: work started from *inside* the exchange
        // inherits its ownership of the lock and is deliberately not deferred.
        using var sendNow = new ManualResetEventSlim(false);
        using var sent = new ManualResetEventSlim(false);

        var sender = new Thread(() =>
        {
            sendNow.Wait(DeadlockBudget);
            device.Send(ScpiMessageProducer.SetDioPortState(4, 1));
            sent.Set();
        })
        {
            IsBackground = true,
        };
        sender.Start();

        var exchange = device.RunTextExchangeAsync(() => sendNow.Set());

        Assert.True(sent.Wait(DeadlockBudget), "The sending thread never ran.");
        Assert.DoesNotContain(transport.Writes, w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        await exchange.WaitAsync(DeadlockBudget);
        await WaitForWriteAsync(transport, "DIO:PORt:STATe");

        Assert.True(sender.Join(TimeSpan.FromSeconds(5)));

        device.Disconnect();
    }

    [Fact]
    public async Task Send_FromInsideATextExchange_GoesStraightOut()
    {
        // The mirror of Send_FromTheOwningFlow_GoesStraightOut, for the lock acquisition the text
        // exchange makes for itself rather than the one RunExclusiveAsync makes. Every text query
        // depends on it: the setup action's whole job is to Send, and if the exchange's own flow
        // does not register as the lock's owner then its command is parked by the very deferral it
        // just switched on. The command reaches the device only after the exchange has closed, so
        // the exchange collects nothing and reports an empty result — indistinguishable, from the
        // caller's side, from a device that went silent.
        //
        // The claim is written into an AsyncLocal, which propagates from the writing frame to that
        // frame's callees and no further. Sending from inside the setup action, after an await, is
        // what pins both halves of that: the exchange's flow owns the lock, and the ownership
        // survives the thread hop.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Self-sending Device", transport);
        device.Connect();

        await device.RunTextExchangeAsync(async _ =>
        {
            device.Send(ScpiMessageProducer.SetDioPortState(4, 1));

            // Fails here, inside the exchange, if the send was parked — rather than passing later
            // on the backlog the exchange flushes on its way out.
            await WaitForWriteAsync(transport, "DIO:PORt:STATe");
        }).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task TextExchange_ReEnteredFromItsOwnSetupAction_IsRejected()
    {
        // DaqifiDeviceTextCommandLockTests covers this guard by planting the flag through
        // reflection, which exercises only the reading half. This drives the real thing: the flag
        // has to be written where the setup action can see it.
        //
        // Getting that wrong does not deadlock, which is what makes it worth pinning. The nested
        // call would find the lock already held by its own flow, decline to wait for it, and run —
        // a second consumer swap on a stream mid-swap, which is the framing corruption the guard
        // was added to prevent. It fails silently, as mangled replies, not as a hang.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Re-entering Device", transport);
        device.Connect();

        Exception? nested = null;

        await device.RunTextExchangeAsync(async ct =>
        {
            nested = await Record.ExceptionAsync(() => device.RunTextExchangeAsync(() => { }, ct));
        }).WaitAsync(DeadlockBudget);

        Assert.IsType<InvalidOperationException>(nested);
        Assert.Contains("not re-entrant", nested!.Message);

        device.Disconnect();
    }

    [Fact]
    public async Task TextExchange_CancelledWhileTheOutboundQueueDrains_DoesNotResubscribeTheConsumer()
    {
        // The drain added for #342 is the one step before the consumer swap that can throw. If it
        // threw from inside the swap's try/finally, that finally would "restart" a consumer that
        // was never stopped — Start() early-returns, but the inbound handler is subscribed again,
        // and every frame from then on is dispatched twice.
        using var transport = new BlockedWriteTransport();
        using var device = new TextExchangeDevice("Draining Device", transport);
        device.Connect();

        // Queue more than the blocked writer can drain, so the exchange is still draining when the
        // token fires.
        for (var i = 0; i < 5; i++)
        {
            device.Send(ScpiMessageProducer.SetDioPortState(i, 1));
        }

        var before = InboundSubscriberCount(device);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.RunTextExchangeAsync(() => { }, cts.Token));

        // A restart that never should have run adds a subscriber; the count must be untouched.
        Assert.Equal(before, InboundSubscriberCount(device));

        transport.ReleaseWrites();
        device.Disconnect();
    }

    private static int InboundSubscriberCount(DaqifiDevice device)
    {
        var consumer = typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device)!;

        var handler = (Delegate?)consumer.GetType()
            .GetField("MessageReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer);

        return handler?.GetInvocationList().Length ?? 0;
    }

    // ── Issue #492: the parked backlog is capped ────────────────────────────────────────────

    [Fact]
    public async Task Send_PastTheBacklogCap_DropsTheOldestAndReplaysTheNewestInOrder()
    {
        using var transport = new RecordingTransport();
        using var device = new CappedDevice("Capped Device", transport, cap: 4);
        device.Connect();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                device.Send(new TaggedBinaryMessage($"m{i:00}"));
            }
        });

        // Read while the operation still holds the device, so this is the backlog itself and not
        // what survived a replay.
        Assert.Equal(4, DeferredBacklogCount(device));
        Assert.Equal(6, device.DroppedDeferredSendCount);

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "m09");

        // Drop-oldest: the four newest survive, still in the order they were sent.
        Assert.Equal(new[] { "m06", "m07", "m08", "m09" }, TaggedWrites(transport));

        device.Disconnect();
    }

    [Fact]
    public async Task Send_WithABacklogUnderTheCap_DropsNothing()
    {
        // The guard on the existing ordering guarantees: everything already tested here parks a
        // handful of messages, and none of it may change because a cap now exists.
        using var transport = new RecordingTransport();
        using var device = new CappedDevice("Uncapped-In-Practice Device", transport, cap: 8);
        device.Connect();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                device.Send(new TaggedBinaryMessage($"m{i:00}"));
            }
        });

        Assert.Equal(5, DeferredBacklogCount(device));

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "m04");

        Assert.Equal(new[] { "m00", "m01", "m02", "m03", "m04" }, TaggedWrites(transport));
        Assert.Equal(0, device.DroppedDeferredSendCount);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_HammeredThroughoutALongOperation_LeavesTheBacklogBounded()
    {
        // The reported failure: an SD card download is allowed thirty minutes, and a UI or agent
        // polling at 10 Hz throughout it parked ~18,000 closures with nothing to stop it. The
        // backlog now sits at the cap no matter how long the hammering goes on.
        using var transport = new RecordingTransport();
        using var device = new CappedDevice("Hammered Device", transport, cap: 4);
        device.Connect();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                device.Send(new TaggedBinaryMessage($"m{i:0000}"));

                // Sampled as it grows, not just at the end: a backlog that ballooned and was
                // trimmed once at the finish would pass an end-state-only assertion.
                Assert.True(
                    DeferredBacklogCount(device) <= 4,
                    $"The backlog grew to {DeferredBacklogCount(device)} after {i + 1} sends.");
            }
        });

        Assert.Equal(4, DeferredBacklogCount(device));
        Assert.Equal(1996, device.DroppedDeferredSendCount);

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_PastTheDefaultCap_UsesDefaultMaxDeferredSends()
    {
        // The production constant, not a test seam: proves the shipped device is the bounded one.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Default Cap Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        const int overflowBy = 6;
        await Task.Run(() =>
        {
            for (var i = 0; i < DaqifiDevice.DefaultMaxDeferredSends + overflowBy; i++)
            {
                device.Send(new TaggedBinaryMessage($"m{i:0000}"));
            }
        });

        Assert.Equal(DaqifiDevice.DefaultMaxDeferredSends, DeferredBacklogCount(device));
        Assert.Equal(overflowBy, device.DroppedDeferredSendCount);

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public void Send_OnAnIdleDevice_NeverDropsAnything()
    {
        using var transport = new RecordingTransport();
        using var device = new CappedDevice("Idle Device", transport, cap: 1);
        device.Connect();

        Assert.Equal(0, device.DroppedDeferredSendCount);

        for (var i = 0; i < 20; i++)
        {
            device.Send(new TaggedBinaryMessage($"m{i:00}"));
        }

        // Nothing owns the device, so nothing was parked and nothing could be dropped — even with
        // a cap of one.
        Assert.Equal(0, device.DroppedDeferredSendCount);
        Assert.Equal(20, TaggedWrites(transport).Count);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_OverflowingTwoOperations_AccumulatesTheCountAndWarnsOncePerBacklog()
    {
        var logger = new CapturingLogger();
        using var transport = new RecordingTransport();
        using var device = new CappedDevice("Twice Overflowed Device", transport, cap: 2, logger: logger);
        device.Connect();

        for (var round = 0; round < 2; round++)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
            }));

            await entered.Task.WaitAsync(DeadlockBudget);

            await Task.Run(() =>
            {
                for (var i = 0; i < 5; i++)
                {
                    device.Send(new TaggedBinaryMessage($"r{round}i{i}"));
                }
            });

            release.SetResult();
            await operation.WaitAsync(DeadlockBudget);

            // The backlog has to be observed empty before the next round, or the second round's
            // overflow would land in the first round's backlog and be reported as one episode.
            await WaitForBacklogDrainAsync(device);
        }

        Assert.Equal(6, device.DroppedDeferredSendCount);

        // One line per overflowing backlog, not one per dropped message: three were dropped in
        // each round, and a warning per drop is how a diagnostic becomes noise nobody reads.
        var overflowWarnings = logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                        && e.Message.Contains("reached its cap", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, overflowWarnings.Count);
        Assert.All(overflowWarnings, w => Assert.Contains("DroppedDeferredSendCount", w.Message, StringComparison.Ordinal));

        device.Disconnect();
    }

    /// <summary>
    /// The live backlog length, read straight off the serializer's own queue.
    /// </summary>
    /// <remarks>
    /// Both hops fail loudly rather than with a <see cref="NullReferenceException"/> (or, worse, a
    /// silent zero that would let every backlog assertion pass vacuously) if the field it names is
    /// ever renamed or moved again.
    /// </remarks>
    private static int DeferredBacklogCount(DaqifiDevice device)
    {
        var serializer = typeof(DaqifiDevice)
                .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(device)
            ?? throw new InvalidOperationException(
                "DaqifiDevice no longer has an _operations field to read the backlog from.");

        var backlogField = serializer.GetType()
                .GetField("_deferredSends", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "OperationSerializer no longer has a _deferredSends field.");

        var backlog = (System.Collections.ICollection?)backlogField.GetValue(serializer);

        return backlog?.Count ?? 0;
    }

    /// <summary>
    /// Waits for the backlog to reach its resting state. The tail of a flush can be handed to a
    /// background drain, so "the operation returned" is not yet "the backlog is gone".
    /// </summary>
    private static async Task WaitForBacklogDrainAsync(DaqifiDevice device)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (DeferredBacklogCount(device) == 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The deferred backlog never drained.");
    }

    /// <summary>The tagged payloads that reached the wire, in order.</summary>
    private static List<string> TaggedWrites(RecordingTransport transport) =>
        transport.Writes
            .Where(w => w.Length > 1 && (w[0] == 'm' || w[0] == 'r'))
            .ToList();

    /// <summary>Shrinks the backlog cap so the drop path is reachable in a bounded test.</summary>
    private sealed class CappedDevice : DaqifiDevice
    {
        private readonly int _cap;

        public CappedDevice(string name, IStreamTransport transport, int cap, ILogger? logger = null)
            : base(name, transport, logger)
        {
            _cap = cap;
        }

        internal override int MaxDeferredSends => _cap;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // ── Qodo round 3: teardown must reset deferral state ────────────────────────────────────

    [Fact]
    public async Task Send_AfterATeardownThatCouldNotTakeTheLock_IsStillDelivered()
    {
        // The silent-loss case. Teardown is bounded: when an in-flight operation does not finish
        // inside the wait, the disconnect proceeds anyway — and the operation that would normally
        // clear the deferral flag on its way out is precisely the one that did not finish. If the
        // flag survives the teardown, every Send() on the NEXT session parks into a backlog with
        // no drainer, and Send() reports success while the command goes nowhere.
        //
        // Driven through the abandoned-lock path on purpose. A clean disconnect resets the flag via
        // the operation's own exit path and would pass either way.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Stranded Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var wedged = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        // A cancelled token makes the teardown give up on the operation lock immediately — the
        // same exit the bounded wait takes when it times out, without waiting out the budget.
        using var giveUpImmediately = new CancellationTokenSource();
        await giveUpImmediately.CancelAsync();
        await device.DisconnectAsync(giveUpImmediately.Token).WaitAsync(DeadlockBudget);

        Assert.False(device.IsConnected);
        Assert.False(wedged.IsCompleted, "The operation was supposed to still be in flight.");

        // New session. The previous one's deferral state must not follow it here.
        device.Connect();
        Assert.True(device.IsConnected);

        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));

        await WaitForWriteAsync(transport, "DIO:PORt:STATe");

        release.SetResult();
        await wedged.WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_FromAFlowThatOutlivedItsSession_NoLongerBypassesDeferral()
    {
        // Ownership of the device is a property of a SESSION. A flow that acquired the operation
        // lock, then had the transport torn down and replaced underneath it, is not the owner of
        // the new session — and must not keep skipping deferral as though it were.
        //
        // The vehicle is the documented fan-out hazard: work started from inside an exclusive
        // block inherits that block's execution context, and with it the block's ownership. Here
        // that inherited ownership is deliberately made to outlive a teardown, which is precisely
        // the stale-owner shape. It must not let the leaked sender write straight through while a
        // completely unrelated operation owns the reconnected device.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Outlived Device", transport);
        device.Connect();

        using var sendNow = new ManualResetEventSlim(false);
        using var sendDone = new ManualResetEventSlim(false);
        Task? leaked = null;

        await device.RunExclusiveAsync(_ =>
        {
            // Inherits this block's context — and therefore its ownership.
            leaked = Task.Run(() =>
            {
                sendNow.Wait(DeadlockBudget);
                device.Send(ScpiMessageProducer.SetDioPortState(5, 1));
                sendDone.Set();
            });
            return Task.CompletedTask;
        }).WaitAsync(DeadlockBudget);

        // Tear the session down and bring a new one up. The leaked sender is now a flow from a
        // session that no longer exists.
        await device.DisconnectAsync().WaitAsync(DeadlockBudget);
        device.Connect();
        Assert.True(device.IsConnected);

        // A genuine operation now owns the reconnected device.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var owner = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        sendNow.Set();
        Assert.True(sendDone.Wait(DeadlockBudget), "The leaked sender never ran.");

        // Still owned by someone else, so the stale flow's message must be parked, not written.
        await Task.Delay(250);
        Assert.DoesNotContain(transport.Writes, w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        release.SetResult();
        await owner.WaitAsync(DeadlockBudget);
        if (leaked != null)
        {
            await leaked.WaitAsync(DeadlockBudget);
        }

        // ...and delivered once that operation finishes.
        await WaitForWriteAsync(transport, "DIO:PORt:STATe");

        device.Disconnect();
    }

    // ── Qodo round 1, finding 1: the drain must cover in-flight writes ──────────────────────

    [Fact]
    public void Producer_WithAWriteInFlight_ReportsQueueEmptyButNotIdle()
    {
        // The trap, stated as an assertion. MessageProducer dequeues BEFORE it writes, so the
        // queue reads empty while the write is still going out. Anything using
        // QueuedMessageCount == 0 as "the wire is quiet" is wrong; IsIdle is the real signal.
        using var stream = new GatedWriteStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();

        producer.Send(ScpiMessageProducer.SetDioPortState(4, 1));

        Assert.True(stream.WaitForWriteToStart(DeadlockBudget), "The producer never started writing.");

        Assert.Equal(0, producer.QueuedMessageCount);
        Assert.False(producer.IsIdle, "IsIdle reported true while a write was still in flight.");

        stream.ReleaseWrites();
    }

    /// <summary>
    /// The same guarantee, sampled at the moment it is hardest to keep. <c>IsIdle</c> is two field
    /// reads, so a caller can straddle the background loop's handover from "queued" to "being
    /// written". Read in the wrong order, those reads land on the queue's after-value and
    /// <c>_draining</c>'s before-value and report idle with a write still in flight — the same
    /// false "all quiet" as <c>QueuedMessageCount == 0</c>, and one
    /// <c>DrainOutboundQueueAsync</c> would act on by swapping consumers mid-write. The test above
    /// holds the producer still and cannot see this; only sampling the handover can, so this
    /// hammers it.
    /// </summary>
    [Fact]
    public void Producer_AtTheHandoverFromQueuedToWriting_NeverReportsAPhantomIdle()
    {
        // ONE producer, sent to many times, rather than a fresh one per attempt. Every
        // MessageProducer.Start() creates a Thread, so an attempt-per-producer loop of any
        // useful density spawns thousands of them for no extra coverage: the background loop
        // returns to waiting on _messageAvailable after each drain, so the next Send re-enters
        // the same queued -> writing handover this is here to sample.
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();

        // Bounded by TIME, not by a fixed count. A count is the wrong knob for a race sampler:
        // it makes a slow CI runner pay for the fast one's density. This keeps the cost fixed
        // and lets a fast machine take more samples inside it.
        var samplingBudget = TimeSpan.FromSeconds(2);
        var clock = Stopwatch.StartNew();
        var attempts = 0;
        var phantomIdles = 0;

        while (clock.Elapsed < samplingBudget)
        {
            attempts++;
            producer.Send(ScpiMessageProducer.SetDioPortState(4, 1));

            // Spun rather than slept: the window is a handful of instructions wide, and a poll
            // that sleeps between reads steps straight over it. Bounded all the same -- an empty
            // spin with no deadline turns a regression that stops the producer idling into a
            // hung CI job burning a core, instead of a failure with a name.
            var spin = Stopwatch.StartNew();
            while (!producer.IsIdle)
            {
                Assert.True(spin.Elapsed < DeadlockBudget,
                    $"The producer never reported idle within {DeadlockBudget.TotalSeconds:0}s on "
                    + $"attempt {attempts}. Something is keeping it draining, which is a different "
                    + "bug from the phantom idle this test samples for.");
            }

            // Nothing has been sent since, and the one wake that drained it is spent, so a
            // producer that reports idle here must still be idle a moment later.
            if (!producer.IsIdle)
            {
                phantomIdles++;
            }
        }

        Assert.Equal(0, phantomIdles);

        // A race sampler that took almost no samples is not evidence of anything, and would pass
        // silently if the loop above ever became a no-op.
        Assert.True(attempts > 100,
            $"Only {attempts} handover samples fit in {samplingBudget.TotalSeconds:0}s; that is "
            + "too few for the absence of a phantom idle to mean much.");
    }

    [Fact]
    public async Task TextExchange_DoesNotTakeTheStreamWhileAWriteIsStillInFlight()
    {
        // The device-level consequence: swapping the stream's reader mid-write means that
        // command's reply is collected into the exchange's answer. The exchange must still be
        // waiting — protobuf consumer running, stream not taken — while the write is blocked.
        using var transport = new GatedWriteTransport();
        using var device = new SlowDrainTextExchangeDevice("Draining Device", transport);
        device.Connect();

        device.Send(ScpiMessageProducer.SetDioPortState(4, 1));
        Assert.True(transport.WaitForWriteToStart(DeadlockBudget), "The producer never started writing.");

        var exchange = Task.Run(() => device.RunTextExchangeAsync(() => { }));

        // Sampled across the whole window rather than once at the end: a drain that returns early
        // swaps within a few tens of milliseconds and then restarts the consumer when the exchange
        // finishes, so a single late sample would see it running again and prove nothing. The
        // write stays blocked throughout, so with the barrier working the consumer is never
        // stopped at any point here.
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(25);
            Assert.True(
                ConsumerIsRunning(device),
                $"The text exchange took the stream while a command was still being written (sample {i}).");
        }

        transport.ReleaseWrites();
        await exchange.WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Qodo round 1, finding 2: deferred sends must not be overtaken ───────────────────────

    [Fact]
    public async Task Send_ArrivingDuringTheFlush_DoesNotOvertakeAlreadyDeferredMessages()
    {
        // Deferral has to stay on while the parked messages are replayed. If it is switched off
        // first, a send arriving mid-replay goes straight out and lands ahead of messages that
        // were queued before it.
        //
        // Binary payloads on purpose: they take the direct-write path, so each replayed send is a
        // real blocking write and the replay window is wide enough to aim at deterministically.
        using var transport = new GatedWriteTransport(writeDelay: TimeSpan.FromMilliseconds(120));
        using var device = new DaqifiDevice("Ordering Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        // Parked while the operation holds the device.
        foreach (var tag in new[] { "A1", "A2", "A3" })
        {
            await Task.Run(() => device.Send(new TaggedBinaryMessage(tag)));
        }

        // A competitor that fires once the replay is under way. Started before the flush and
        // gated, so it carries none of the flushing flow's context.
        using var replayStarted = new ManualResetEventSlim(false);
        transport.OnWriteStarted = () => replayStarted.Set();

        // The wait's result is captured and asserted, not discarded. If the replay never starts,
        // the competitor must not send at all — otherwise "B came last" would be satisfied by a
        // race that never happened, and a real regression would show up as a flake instead of a
        // failure.
        var replayObserved = false;
        var competitor = new Thread(() =>
        {
            // Shorter than the Join below, so a phase boundary that never fires surfaces as the
            // specific "replay never started" assertion rather than an opaque join timeout.
            replayObserved = replayStarted.Wait(PhaseBoundaryWait);
            if (!replayObserved)
            {
                return;
            }

            device.Send(new TaggedBinaryMessage("B"));
        })
        {
            IsBackground = true,
        };
        competitor.Start();

        transport.ReleaseWrites();
        release.SetResult();

        await operation.WaitAsync(DeadlockBudget);
        Assert.True(competitor.Join(TimeSpan.FromSeconds(10)), "The competing sender never finished.");

        // Join established the happens-before, so this read is safe.
        Assert.True(
            replayObserved,
            "The replay never started, so the competitor never raced it and the ordering assertion below would be vacuous.");

        await WaitForWriteAsync(transport, "B");

        var order = transport.Writes.Where(w => w.Length <= 2).ToList();
        Assert.Equal(new[] { "A1", "A2", "A3", "B" }, order);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_DoesNotBlockWhileALargeBacklogIsBeingFlushed()
    {
        // The flush is bounded so one operation cannot be kept from returning by a fast sender.
        // That bound must NOT be paid for by finishing the last stretch under the deferral gate:
        // Send() takes that same gate, so it would then block on whatever blocking I/O the replay
        // is doing — losing the non-blocking guarantee that is the whole reason deferral was
        // chosen over making Send() wait.
        //
        // The backlog here is deliberately larger than the per-flush bound, so the probe lands in
        // the stretch that runs after the bound is hit.
        using var transport = new GatedWriteTransport(writeDelay: TimeSpan.FromMilliseconds(10));
        using var device = new DaqifiDevice("Backlog Device", transport);
        device.Connect();
        transport.ReleaseWrites();

        const int backlog = 120;
        const int probeAfterWrites = 80; // comfortably past the 64-message per-flush bound

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var i = 0; i < backlog; i++)
            {
                device.Send(new TaggedBinaryMessage($"m{i}"));
            }
        });

        using var deepIntoFlush = new ManualResetEventSlim(false);
        var writes = 0;
        transport.OnWriteStarted = () =>
        {
            if (Interlocked.Increment(ref writes) >= probeAfterWrites)
            {
                deepIntoFlush.Set();
            }
        };

        var probeElapsed = TimeSpan.MaxValue;
        var probeRan = false;
        var probe = new Thread(() =>
        {
            probeRan = deepIntoFlush.Wait(PhaseBoundaryWait);
            if (!probeRan)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            device.Send(new TaggedBinaryMessage("probe"));
            sw.Stop();
            probeElapsed = sw.Elapsed;
        })
        {
            IsBackground = true,
        };
        probe.Start();

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);
        Assert.True(probe.Join(TimeSpan.FromSeconds(30)), "The probing sender never finished.");

        Assert.True(probeRan, "The flush never reached the probe point, so nothing was measured.");
        Assert.True(
            probeElapsed < TimeSpan.FromMilliseconds(150),
            $"Send() blocked for {probeElapsed.TotalMilliseconds:0}ms during the flush; it must never wait on replay I/O.");

        device.Disconnect();
    }

    private static bool ConsumerIsRunning(DaqifiDevice device)
    {
        var consumer = typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);

        return consumer is IMessageConsumer<DaqifiOutMessage> { IsRunning: true };
    }

    /// <summary>A message whose payload is a short ASCII tag, so write order is readable.</summary>
    private sealed class TaggedBinaryMessage : IOutboundMessage<byte[]>
    {
        public TaggedBinaryMessage(string tag) => Data = Encoding.UTF8.GetBytes(tag);

        public byte[] Data { get; set; }

        public byte[] GetBytes() => Data;
    }

    /// <summary>Raises the drain budget so the wait is observable rather than a race.</summary>
    private sealed class SlowDrainTextExchangeDevice : DaqifiDevice
    {
        public SlowDrainTextExchangeDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        internal override TimeSpan OutboundDrainWait => TimeSpan.FromSeconds(5);

        public Task<IReadOnlyList<string>> RunTextExchangeAsync(Action setupAction) =>
            ExecuteTextCommandAsync(setupAction, responseTimeoutMs: 300, completionTimeoutMs: 100);
    }

    // ── The inbound path stays clear ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_DoesNotBlockInboundChannelWork()
    {
        // Streaming callbacks, the reader loop and frame decode must never wait on the operation
        // lock — a control operation must not stall a live stream. Channel snapshotting is the
        // device-level state those paths touch.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Streaming-through Device", transport);
        device.Connect();

        await device.RunExclusiveAsync(async ct =>
        {
            var inbound = Task.Run(() =>
            {
                var seen = 0;
                for (var i = 0; i < 200; i++)
                {
                    seen += device.GetChannelsSnapshot().Count;
                }

                return seen;
            }, ct);

            await inbound.WaitAsync(TimeSpan.FromSeconds(5));
        }).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static Task WaitForWriteAsync(RecordingTransport transport, string fragment) =>
        WaitForWriteAsync(() => transport.Writes, fragment);

    private static Task WaitForWriteAsync(GatedWriteTransport transport, string fragment) =>
        WaitForWriteAsync(() => transport.Writes, fragment);

    private static async Task WaitForWriteAsync(Func<IReadOnlyList<string>> writes, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (writes().Any(w => w.Contains(fragment, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"'{fragment}' never reached the wire. Writes: {string.Join(" | ", writes())}");
    }

    /// <summary>Exposes the protected text-exchange entry point.</summary>
    private sealed class TextExchangeDevice : DaqifiDevice
    {
        public TextExchangeDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Action setupAction,
            CancellationToken cancellationToken = default) =>
            ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: 300,
                completionTimeoutMs: 100,
                cancellationToken: cancellationToken);

        /// <summary>
        /// The async-setup overload, so a test can observe the wire from <i>inside</i> the exchange
        /// rather than only after it has closed.
        /// </summary>
        public Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Func<CancellationToken, Task> setupActionAsync,
            CancellationToken cancellationToken = default) =>
            ExecuteTextCommandAsync(
                setupActionAsync,
                responseTimeoutMs: 300,
                completionTimeoutMs: 100,
                cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A stream that parks inside <see cref="Write"/> until released, and reports when a write has
    /// actually begun — the state where the queue is empty but the wire is not yet quiet.
    /// </summary>
    private sealed class GatedWriteStream : Stream
    {
        private readonly ManualResetEventSlim _released = new(false);
        private readonly ManualResetEventSlim _writeStarted = new(false);
        private readonly List<string> _writes = new();
        private readonly object _gate = new();
        private readonly TimeSpan _writeDelay;

        public GatedWriteStream(TimeSpan? writeDelay = null) => _writeDelay = writeDelay ?? TimeSpan.Zero;

        /// <summary>Invoked on the writing thread each time a write begins.</summary>
        public Action? OnWriteStarted { get; set; }

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return _writes.ToList();
                }
            }
        }

        public bool WaitForWriteToStart(TimeSpan timeout) => _writeStarted.Wait(timeout);

        public void ReleaseWrites() => _released.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(5);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _writeStarted.Set();
            OnWriteStarted?.Invoke();

            _released.Wait(TimeSpan.FromSeconds(10));

            if (_writeDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_writeDelay);
            }

            lock (_gate)
            {
                _writes.Add(Encoding.UTF8.GetString(buffer, offset, count));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Set();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Transport over a <see cref="GatedWriteStream"/>.</summary>
    private sealed class GatedWriteTransport : IStreamTransport
    {
        private readonly GatedWriteStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public GatedWriteTransport(TimeSpan? writeDelay = null) => _stream = new GatedWriteStream(writeDelay);

        public IReadOnlyList<string> Writes => _stream.Writes;

        public Action? OnWriteStarted
        {
            get => _stream.OnWriteStarted;
            set => _stream.OnWriteStarted = value;
        }

        public bool WaitForWriteToStart(TimeSpan timeout) => _stream.WaitForWriteToStart(timeout);

        public void ReleaseWrites() => _stream.ReleaseWrites();

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(GatedWriteTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Gated: Connected" : "Gated: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GatedWriteTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _stream.ReleaseWrites();
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _stream.ReleaseWrites();
            _isConnected = false;
            _disposed = true;
        }
    }

    /// <summary>
    /// Transport whose writes block until released, so the producer's queue stays non-empty and a
    /// text exchange is guaranteed to still be draining it when a cancellation lands.
    /// </summary>
    private sealed class BlockedWriteTransport : IStreamTransport
    {
        private readonly BlockingStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(BlockedWriteTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Blocked: Connected" : "Blocked: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void ReleaseWrites() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BlockedWriteTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _stream.Release();
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _stream.Release();
            _isConnected = false;
            _disposed = true;
        }

        private sealed class BlockingStream : Stream
        {
            private readonly ManualResetEventSlim _released = new(false);

            public void Release() => _released.Set();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(5);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                _released.Wait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Transport over a stream that records every write and never has anything to read, so tests
    /// can assert on exactly what reached the wire and when.
    /// </summary>
    private sealed class RecordingTransport : IStreamTransport
    {
        private readonly RecordingStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public IReadOnlyList<string> Writes => _stream.Writes;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(RecordingTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Recording: Connected" : "Recording: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecordingTransport));
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

        private sealed class RecordingStream : Stream
        {
            private readonly List<string> _writes = new();
            private readonly object _gate = new();

            public IReadOnlyList<string> Writes
            {
                get
                {
                    lock (_gate)
                    {
                        return _writes.ToList();
                    }
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // Nothing to read; back off so the consumer's reader loop doesn't spin.
                Thread.Sleep(5);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    _writes.Add(Encoding.UTF8.GetString(buffer, offset, count));
                }
            }
        }
    }
}
