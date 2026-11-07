using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Daqifi.Core.Tests.Communication.Producers;

/// <summary>
/// Issue #933: one stream must never have two writer threads. A stop's join is time-bounded, so a
/// writer inside a slow write can outlive it; these pin what <c>Start()</c> does about that, and
/// that a stop or start made from the writer's own callback neither joins itself nor stalls.
/// Every test also asserts that the stream never saw two writes at once.
/// </summary>
public class MessageProducerRestartTests
{
    /// <summary>
    /// Only ever spent by a failing test. Nothing here sleeps; each wait is on a gate.
    /// </summary>
    private static readonly TimeSpan HangBreaker = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a surplus writer gets to report its exit. A stop only joins the thread the producer
    /// knows about, so a surplus one reports after it. Too short can only miss a bug, not invent one.
    /// </summary>
    private static readonly TimeSpan SurplusWriterSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The producer's own bound on a stop's join and on <c>Start()</c>'s wait for a stale writer.
    /// A call that really waited on the writer takes at least this long.
    /// </summary>
    private const int ProducerWaitMs = 1000;

    /// <summary>
    /// The issue's failure: Start, Stop during a write, let the 1 s join time out, Start again while
    /// the old thread is still writing. It must refuse, not start a second thread, and accept again
    /// once the old thread has gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_AfterATimedOutStop_WhenTheOldThreadHasNotExited_RefusesUntilItHas(bool stopSafely)
    {
        using var stream = new GatedWriteStream();
        using var producer = new MessageProducer<string>(stream);
        try
        {
            producer.Start();
            producer.Send(new ScpiMessage("FIRST"));
            Assert.True(stream.WaitForWrites(1, HangBreaker), "The first write never started.");

            if (stopSafely)
            {
                Assert.False(producer.StopSafely(50));
            }
            else
            {
                producer.Stop();
            }

            Assert.False(producer.IsRunning);

            Assert.Throws<ProducerThreadNotExitedException>(() => producer.Start());

            Assert.False(producer.IsRunning);
            Assert.Throws<InvalidOperationException>(() => producer.Send(new ScpiMessage("REFUSED")));
            Assert.Equal(1, stream.WriteCount);

            // The refusal holds only as long as the thread does.
            var oldWriter = stream.Writers.Single();
            stream.ReleaseHeldWrite();
            Assert.True(oldWriter.Join(HangBreaker), "The old writer thread never exited.");

            producer.Start();
            Assert.True(producer.IsRunning);
            producer.Send(new ScpiMessage("SECOND"));
            Assert.True(stream.WaitForWrites(2, HangBreaker), "The restarted producer never wrote.");

            Assert.Equal(2, stream.Writers.Count);
            Assert.Equal(1, stream.MaxConcurrentWrites);
        }
        finally
        {
            stream.ReleaseHeldWrite();
        }
    }

    /// <summary>
    /// The old thread exits while <c>Start()</c> is waiting for it, so the restart works. The second
    /// message goes out from the starter's thread, so on code that does not wait it reaches the
    /// stream while the first write is still held, which is the overlap this exists to catch.
    /// </summary>
    [Fact]
    public void Start_AfterATimedOutStop_WhenTheOldThreadExitsDuringTheWait_Succeeds()
    {
        using var stream = new GatedWriteStream();
        using var producer = new MessageProducer<string>(stream);
        try
        {
            StopWithTheFirstWriteHeld(producer, stream);

            var starter = RunOnThread(() =>
            {
                producer.Start();
                producer.Send(new ScpiMessage("SECOND"));
            });

            AwaitParkedOrOverlap(stream, starter);
            stream.ReleaseHeldWrite();

            starter.AssertCompleted();
            Assert.True(stream.WaitForWrites(2, HangBreaker), "The restarted producer never wrote.");
            Assert.True(producer.IsRunning);
            Assert.Equal(1, stream.MaxConcurrentWrites);
        }
        finally
        {
            stream.ReleaseHeldWrite();
        }
    }

    /// <summary>
    /// Two starters both wait out the same stale thread and then both find it gone. Only one may
    /// start a writer, and neither is refused, because the condition each was waiting on came true.
    /// </summary>
    [Fact]
    public void ConcurrentStarts_WaitingOnTheSameStaleThread_StartOneThread()
    {
        var logger = new ExitCountingLogger();
        using var stream = new GatedWriteStream();
        using var producer = new MessageProducer<string>(stream, logger);
        try
        {
            StopWithTheFirstWriteHeld(producer, stream);

            void StartThenSend()
            {
                producer.Start();
                producer.Send(new ScpiMessage("SECOND"));
            }

            var first = RunOnThread(StartThenSend);
            var second = RunOnThread(StartThenSend);

            AwaitParkedOrOverlap(stream, first, second);
            stream.ReleaseHeldWrite();

            first.AssertCompleted();
            second.AssertCompleted();
            Assert.True(stream.WaitForWrites(3, HangBreaker), "The restarted producer never wrote.");

            // The stale writer plus exactly one new one.
            StopAndAssertWriterThreadCount(producer, logger, 2);
            Assert.Equal(1, stream.MaxConcurrentWrites);
        }
        finally
        {
            stream.ReleaseHeldWrite();
        }
    }

    /// <summary>
    /// A plain race of <c>Start()</c> against <c>Start()</c>. Sampled over a fixed time budget for
    /// the same reason as the producer's other race samplers: a count makes a slow runner pay for a
    /// fast one's density.
    /// </summary>
    [Fact]
    public void ConcurrentStarts_StartOneThread()
    {
        const int starterCount = 4;

        var logger = new ExitCountingLogger();
        using var stream = new GatedWriteStream(holdFirstWrite: false);
        using var producer = new MessageProducer<string>(stream, logger);
        using var barrier = new Barrier(starterCount + 1);
        var finished = false;

        var starters = Enumerable.Range(0, starterCount)
            .Select(_ => RunOnThread(() =>
            {
                while (true)
                {
                    barrier.SignalAndWait();
                    if (Volatile.Read(ref finished))
                    {
                        return;
                    }

                    producer.Start();
                    barrier.SignalAndWait();
                }
            }))
            .ToArray();

        var budget = Stopwatch.StartNew();
        var attempts = 0;
        while (budget.Elapsed < TimeSpan.FromSeconds(1))
        {
            attempts++;

            barrier.SignalAndWait(); // release the starters
            barrier.SignalAndWait(); // all have returned from Start()
            Assert.True(producer.IsRunning);

            producer.Send(new ScpiMessage("PING"));
            Assert.True(stream.WaitForWrites(attempts, HangBreaker), "The producer never wrote.");

            StopTwice(producer);
        }

        Volatile.Write(ref finished, true);
        barrier.SignalAndWait();
        foreach (var starter in starters)
        {
            starter.AssertCompleted();
        }

        StopAndAssertWriterThreadCount(producer, logger, attempts);
        Assert.Equal(1, stream.MaxConcurrentWrites);
    }

    /// <summary>
    /// A stop from the writer's own callback cannot join that same thread. It used to wait out the
    /// whole join on itself, stalling every message queued behind it.
    /// </summary>
    [Fact]
    public void Stop_FromTheWritersOwnCallback_DoesNotJoinItself()
    {
        using var stream = new GatedWriteStream(holdFirstWrite: false, failWrites: true);
        using var producer = new MessageProducer<string>(stream);
        var callback = new CallbackProbe(() => producer.Stop());
        producer.SendFailed += callback.OnSendFailed;

        producer.Start();
        producer.Send(new ScpiMessage("FIRST"));

        callback.AssertRan();
        Assert.Null(callback.Thrown);
        Assert.True(callback.ElapsedMs < ProducerWaitMs, $"Stop() stalled {callback.ElapsedMs} ms on itself.");
        Assert.False(producer.IsRunning);

        Assert.True(callback.Writer!.Join(HangBreaker), "The writer thread never exited after its own stop.");
        producer.SendFailed -= callback.OnSendFailed;

        producer.Start();
        Assert.True(producer.IsRunning);
        Assert.Equal(1, stream.MaxConcurrentWrites);
    }

    /// <summary>
    /// The writer can never see itself go idle, so there is nothing for this to wait for. The large
    /// timeout makes a regression unmistakable rather than a matter of a tight bound.
    /// </summary>
    [Fact]
    public void StopSafely_FromTheWritersOwnCallback_DoesNotWaitOnItself()
    {
        const int timeoutMs = 30_000;

        using var stream = new GatedWriteStream(holdFirstWrite: false, failWrites: true);
        using var producer = new MessageProducer<string>(stream);
        var stopped = true;
        var callback = new CallbackProbe(() => stopped = producer.StopSafely(timeoutMs));
        producer.SendFailed += callback.OnSendFailed;

        producer.Start();
        producer.Send(new ScpiMessage("FIRST"));

        callback.AssertRan();
        Assert.Null(callback.Thrown);
        Assert.False(stopped);
        Assert.True(callback.ElapsedMs < timeoutMs / 2, $"StopSafely() waited {callback.ElapsedMs} ms on itself.");
        Assert.True(callback.Writer!.Join(HangBreaker), "The writer thread never exited after its own stop.");
        Assert.Equal(1, stream.MaxConcurrentWrites);
    }

    /// <summary>
    /// A start from the writer's own callback, after a stop was requested: it can never see itself
    /// exit, so waiting out the grace would only delay the same refusal.
    /// </summary>
    [Fact]
    public void Start_FromTheWritersOwnCallbackAfterAStop_RefusesWithoutWaiting()
    {
        using var stream = new GatedWriteStream(holdFirstWrite: false, failWrites: true);
        using var producer = new MessageProducer<string>(stream);
        var callback = new CallbackProbe(() =>
        {
            producer.Stop();
            producer.Start();
        });
        producer.SendFailed += callback.OnSendFailed;

        producer.Start();
        producer.Send(new ScpiMessage("FIRST"));

        callback.AssertRan();
        Assert.IsType<ProducerThreadNotExitedException>(callback.Thrown);
        Assert.True(callback.ElapsedMs < ProducerWaitMs, $"Start() stalled {callback.ElapsedMs} ms on itself.");
        Assert.False(producer.IsRunning);
        Assert.True(callback.Writer!.Join(HangBreaker), "The writer thread never exited after its own stop.");
        Assert.Equal(1, stream.MaxConcurrentWrites);
    }

    [Fact]
    public void Dispose_FromTheWritersOwnCallback_DoesNotStallAndTheThreadExits()
    {
        const int disposeStallMs = 1000;

        using var stream = new GatedWriteStream(holdFirstWrite: false, failWrites: true);
        var producer = new MessageProducer<string>(stream);
        var callback = new CallbackProbe(producer.Dispose);
        producer.SendFailed += callback.OnSendFailed;

        producer.Start();
        producer.Send(new ScpiMessage("FIRST"));

        callback.AssertRan();
        Assert.Null(callback.Thrown);
        Assert.True(callback.ElapsedMs < disposeStallMs, $"Dispose() stalled {callback.ElapsedMs} ms on itself.");
        Assert.True(callback.Writer!.Join(HangBreaker), "The writer thread never exited after its own dispose.");
        Assert.Throws<ObjectDisposedException>(() => producer.Start());
        Assert.Equal(1, stream.MaxConcurrentWrites);
    }

    /// <summary>
    /// <c>Start()</c> waits for the stale thread with no lock held. If it held one, a stop from that
    /// thread's callback would queue behind the wait, the thread could not exit, and the wait would
    /// time out into a refusal caused by the wait itself.
    /// </summary>
    [Fact]
    public void Start_WaitingOnAStaleThread_DoesNotBlockThatThreadsOwnCallback()
    {
        using var stream = new GatedWriteStream(holdFirstWrite: false, failWrites: true);
        using var producer = new MessageProducer<string>(stream);
        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        var callback = new CallbackProbe(() =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
            producer.Stop();
        });
        producer.SendFailed += callback.OnSendFailed;

        try
        {
            producer.Start();
            producer.Send(new ScpiMessage("FIRST"));
            Assert.True(callbackEntered.Wait(HangBreaker), "The callback never ran.");

            // Times out: the writer is sitting in the callback.
            producer.Stop();

            var starter = RunOnThread(producer.Start);
            AwaitParkedOrOverlap(stream, starter);
            releaseCallback.Set();

            starter.AssertCompleted();
            callback.AssertRan();
            Assert.Null(callback.Thrown);
            Assert.True(producer.IsRunning);
            Assert.Equal(1, stream.MaxConcurrentWrites);
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    /// <summary>
    /// Leaves the producer stopped by a join that timed out, with its only write still in the stream.
    /// </summary>
    private static void StopWithTheFirstWriteHeld(MessageProducer<string> producer, GatedWriteStream stream)
    {
        producer.Start();
        producer.Send(new ScpiMessage("FIRST"));
        Assert.True(stream.WaitForWrites(1, HangBreaker), "The first write never started.");

        producer.Stop();

        Assert.False(producer.IsRunning);
        Assert.Equal(1, stream.WriteCount);
    }

    /// <summary>
    /// The second stop signals the wake event again. A surplus writer can lose the first signal to
    /// its twin's <c>Reset()</c>, and would then stay parked and never report its exit.
    /// </summary>
    private static void StopTwice(MessageProducer<string> producer)
    {
        producer.Stop();
        producer.Stop();
    }

    /// <summary>
    /// Each writer thread logs its clean exit exactly once, so the exit count is the thread count.
    /// </summary>
    private static void StopAndAssertWriterThreadCount(
        MessageProducer<string> producer, ExitCountingLogger logger, int expected)
    {
        StopTwice(producer);

        SpinWait.SpinUntil(() => logger.ExitCount > expected, SurplusWriterSettle);
        Assert.Equal(expected, logger.ExitCount);
    }

    /// <summary>
    /// Waits until every thread is parked inside <c>Start()</c>'s wait, or has finished, or has got a
    /// second write onto the stream. The last two are how code that does not wait shows up, and the
    /// caller's overlap assertion then reports it.
    /// </summary>
    private static void AwaitParkedOrOverlap(GatedWriteStream stream, params TestThread[] threads)
    {
        Assert.True(
            SpinWait.SpinUntil(
                () => stream.WriteCount >= 2 || threads.All(t => t.IsParkedOrFinished),
                HangBreaker),
            "Start() neither waited for the old thread nor let a write through.");
    }

    private static TestThread RunOnThread(Action action) => new(action);

    /// <summary>
    /// A dedicated thread whose failure is reported to the test rather than lost. Not a thread-pool
    /// item, so that <see cref="IsParkedOrFinished"/> means something.
    /// </summary>
    private sealed class TestThread
    {
        private readonly Thread _thread;
        private Exception? _thrown;

        public TestThread(Action action)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _thrown = ex;
                }
            })
            {
                IsBackground = true
            };
            _thread.Start();
        }

        public bool IsParkedOrFinished =>
            (_thread.ThreadState & (System.Threading.ThreadState.WaitSleepJoin | System.Threading.ThreadState.Stopped)) != 0;

        public void AssertCompleted()
        {
            Assert.True(_thread.Join(HangBreaker), "The thread never finished.");
            Assert.Null(_thrown);
        }
    }

    /// <summary>
    /// A <c>SendFailed</c> handler that runs a lifecycle call on the writer thread and records how
    /// that went. The producer swallows whatever a handler throws, so the test has to catch it here.
    /// </summary>
    private sealed class CallbackProbe(Action action)
    {
        private readonly ManualResetEventSlim _ran = new(false);
        private int _claimed;

        public Thread? Writer { get; private set; }

        public Exception? Thrown { get; private set; }

        public long ElapsedMs { get; private set; }

        public void OnSendFailed(object? sender, MessageSendFailedEventArgs<string> e)
        {
            if (Interlocked.Exchange(ref _claimed, 1) == 1)
            {
                return;
            }

            Writer = Thread.CurrentThread;
            var clock = Stopwatch.StartNew();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Thrown = ex;
            }

            ElapsedMs = clock.ElapsedMilliseconds;
            _ran.Set();
        }

        public void AssertRan() => Assert.True(_ran.Wait(HangBreaker), "The callback never finished.");
    }

    /// <summary>
    /// Counts the one clean-exit line a writer thread logs on its way out.
    /// </summary>
    private sealed class ExitCountingLogger : ILogger<MessageProducer<string>>
    {
        private int _exitCount;

        public int ExitCount => Volatile.Read(ref _exitCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("exited cleanly", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _exitCount);
            }
        }
    }

    /// <summary>
    /// A stream that can hold its first write open until released, the slow write the issue needs,
    /// and that records every thread that wrote and the most writes it ever saw at once.
    /// </summary>
    private sealed class GatedWriteStream(bool holdFirstWrite = true, bool failWrites = false) : Stream
    {
        private readonly object _sync = new();
        private readonly List<Thread> _writers = [];
        private readonly ManualResetEventSlim _release = new(false);
        private int _writeCount;
        private int _inWrite;
        private int _maxConcurrentWrites;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public int MaxConcurrentWrites => Volatile.Read(ref _maxConcurrentWrites);

        public IReadOnlyList<Thread> Writers
        {
            get
            {
                lock (_sync)
                {
                    return _writers.ToArray();
                }
            }
        }

        public void ReleaseHeldWrite() => _release.Set();

        public bool WaitForWrites(int count, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => WriteCount >= count, timeout);

        public override void Write(byte[] buffer, int offset, int count)
        {
            var concurrent = Interlocked.Increment(ref _inWrite);
            try
            {
                int writeNumber;
                lock (_sync)
                {
                    _maxConcurrentWrites = Math.Max(_maxConcurrentWrites, concurrent);
                    if (!_writers.Contains(Thread.CurrentThread))
                    {
                        _writers.Add(Thread.CurrentThread);
                    }

                    writeNumber = ++_writeCount;
                }

                if (holdFirstWrite && writeNumber == 1)
                {
                    _release.Wait();
                }

                if (failWrites)
                {
                    throw new IOException("Simulated write failure.");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inWrite);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _release.Set();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
