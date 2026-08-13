using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using System.Diagnostics;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Producers;

/// <summary>
/// Issue #491, item 1: the background loop used to wake ten times a second for the life of every
/// connected device, whether or not there was anything to send. These pin both halves of the fix —
/// that an idle producer costs nothing, and that removing the poll did not cost a wakeup that the
/// timeout had been covering for.
/// </summary>
public class MessageProducerIdleWakeupTests
{
    /// <summary>
    /// Long enough that the old 100 ms poll would have produced several wakeups, short enough not
    /// to slow the suite down noticeably.
    /// </summary>
    private const int IdleObservationMs = 400;

    [Fact]
    public void AnIdleProducer_NeverWakes()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        Thread.Sleep(IdleObservationMs);

        // The old loop would sit at roughly IdleObservationMs / 100 here.
        Assert.Equal(0L, producer.WakeCount);
    }

    [Fact]
    public void AfterDrainingAMessage_TheProducerGoesBackToNotWaking()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        producer.Send(new ScpiMessage("TEST:ONE"));
        WaitUntilIdle(producer);

        var wakesAfterTheSend = producer.WakeCount;
        Assert.InRange(wakesAfterTheSend, 1L, 2L);

        Thread.Sleep(IdleObservationMs);

        Assert.Equal(wakesAfterTheSend, producer.WakeCount);
    }

    [Fact]
    public void EachSend_WakesTheLoop()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();

        for (var i = 0; i < 5; i++)
        {
            producer.Send(new ScpiMessage($"TEST:{i}"));
            WaitUntilIdle(producer);
        }

        Assert.True(producer.StopSafely(2000));
        Assert.True(producer.WakeCount >= 5,
            $"Expected at least one wakeup per send, saw {producer.WakeCount}.");
    }

    /// <summary>
    /// The lost-wakeup case the 100 ms timeout was papering over: a message enqueued from inside the
    /// write of the message before it, i.e. after the drain loop has already looked at the queue.
    /// With the wait now unbounded, a signal dropped here would strand the message forever rather
    /// than delaying it by a tenth of a second.
    /// </summary>
    [Fact]
    public void AMessageEnqueuedFromInsideTheWrite_IsStillSent()
    {
        var followUpSent = new ManualResetEventSlim(false);
        MessageProducer<string>? producer = null;
        var enqueuedFollowUp = false;

        using var stream = new CallbackStream(payload =>
        {
            if (!enqueuedFollowUp)
            {
                enqueuedFollowUp = true;
                producer!.Send(new ScpiMessage("TEST:FOLLOWUP"));
                return;
            }

            if (payload.Contains("FOLLOWUP", StringComparison.Ordinal))
            {
                followUpSent.Set();
            }
        });

        producer = new MessageProducer<string>(stream);
        try
        {
            producer.Start();
            producer.Send(new ScpiMessage("TEST:FIRST"));

            Assert.True(followUpSent.Wait(TimeSpan.FromSeconds(5)),
                "The message enqueued during the first write was never written.");
        }
        finally
        {
            producer.Dispose();
            followUpSent.Dispose();
        }
    }

    /// <summary>
    /// The counter spans the instance, not a run: a restarted producer keeps counting from where it
    /// was. Pinned because the alternative — zeroing it in <c>Start()</c> — races a previous
    /// background thread that a timed-out join left alive and still incrementing.
    /// </summary>
    [Fact]
    public void WakeCount_IsCumulativeAcrossRestarts()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        producer.Send(new ScpiMessage("TEST:BEFORE"));
        WaitUntilIdle(producer);
        Assert.True(producer.StopSafely(2000));

        var wakesBeforeTheRestart = producer.WakeCount;
        Assert.True(wakesBeforeTheRestart >= 1);

        producer.Start();
        producer.Send(new ScpiMessage("TEST:AFTER"));
        WaitUntilIdle(producer);
        Assert.True(producer.StopSafely(2000));

        Assert.True(producer.WakeCount > wakesBeforeTheRestart,
            $"Expected the count to carry over and grow; it went {wakesBeforeTheRestart} -> {producer.WakeCount}.");
    }

    /// <summary>
    /// The failure mode the removed timeout would have masked. A signal lost in the window between
    /// the drain's last look at the queue and the loop parking again strands whatever was enqueued
    /// there — and with an unbounded wait, strands it until the <em>next</em> send rather than for
    /// 100 ms. Each iteration therefore confirms its own message reached the stream before sending
    /// the next, so a single stranded message fails the test instead of being swept up later.
    /// </summary>
    [Fact]
    public void SendsThatLandAtEveryPointInTheLoopCycle_AreNeverStranded()
    {
        var written = 0;

        // Declared first so it is disposed last: the producer's background thread signals it, and
        // that thread only stops when the producer below is disposed.
        using var delivered = new AutoResetEvent(false);
        using var stream = new CallbackStream(_ =>
        {
            Interlocked.Increment(ref written);
            delivered.Set();
        });
        using var producer = new MessageProducer<string>(stream);

        producer.Start();

        const int sends = 200;
        for (var i = 0; i < sends; i++)
        {
            // Vary where the enqueue lands relative to the drain/park cycle. A plain tight loop
            // always arrives while the loop is still draining, which is the easy case.
            if (i % 3 == 0)
            {
                Thread.Sleep(1);
            }
            else if (i % 3 == 1)
            {
                Thread.SpinWait(200);
            }

            producer.Send(new ScpiMessage($"TEST:{i}"));

            Assert.True(delivered.WaitOne(TimeSpan.FromSeconds(2)),
                $"Message {i} was never written; a wakeup was lost.");
        }

        Assert.Equal(sends, Volatile.Read(ref written));
    }

    /// <summary>
    /// Both stops set the event before joining, so a loop parked in an unbounded wait still exits
    /// promptly. Bounded well under the 1 s join so a regression shows up as a failure rather than
    /// as a slow suite.
    /// </summary>
    [Fact]
    public void Stop_FromAParkedLoop_ReturnsPromptly()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        Thread.Sleep(50);

        var sw = Stopwatch.StartNew();
        producer.Stop();
        sw.Stop();

        Assert.False(producer.IsRunning);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Stop() took {sw.ElapsedMilliseconds} ms from a parked loop.");
    }

    [Fact]
    public void StopSafely_FromAParkedLoop_ReturnsPromptly()
    {
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        Thread.Sleep(50);

        var sw = Stopwatch.StartNew();
        var stopped = producer.StopSafely(1000);
        sw.Stop();

        Assert.True(stopped);
        Assert.False(producer.IsRunning);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"StopSafely() took {sw.ElapsedMilliseconds} ms from a parked loop.");
    }

    private static void WaitUntilIdle(MessageProducer<string> producer)
    {
        var deadline = Stopwatch.StartNew();
        while (!producer.IsIdle && deadline.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(5);
        }

        Assert.True(producer.IsIdle, "The producer never drained its queue.");
    }

    /// <summary>
    /// A stream that hands each written payload to a callback, so a test can act at the exact
    /// moment the producer is mid-write.
    /// </summary>
    private sealed class CallbackStream(Action<string> onWrite) : Stream
    {
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

        public override void Write(byte[] buffer, int offset, int count) =>
            onWrite(Encoding.ASCII.GetString(buffer, offset, count));
    }
}
