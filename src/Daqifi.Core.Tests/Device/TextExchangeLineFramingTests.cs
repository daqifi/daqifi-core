using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for how the SCPI text exchange frames the device's reply (issue #538).
/// </summary>
/// <remarks>
/// The exchange decides that the device has answered by counting the lines its parser produces, so
/// a reply the parser cannot turn into a line is a reply the exchange cannot see: it waits out its
/// whole first-response timeout for an answer that is already in the buffer, holding the device
/// operation lock the entire time. The DAQiFi firmware sends two such shapes — a bare-LF reply
/// (<c>SYSTem:LOG:CLEar</c>, <c>SYSTem:LOG:TEST</c>) and a blank line (the terminator of a
/// <c>SYSTem:LOG?</c> dump, which is the whole reply when the log is empty). Measured on a bench
/// Nq1 running firmware 3.7.2: both cost 1.5-2.0 s on top of the ~1.05 s a CRLF reply takes.
/// </remarks>
public class TextExchangeLineFramingTests
{
    /// <summary>
    /// First-response timeout used by the timing assertions. Large enough that "recognised the
    /// reply" and "waited the timeout out" are far apart even on a loaded CI machine.
    /// </summary>
    private const int ResponseTimeoutMs = 3000;

    private const int CompletionTimeoutMs = 150;

    /// <summary>
    /// Completion timeout for the fragmented-reply test, and the two constants below it are sized
    /// against it. Two things have to be true at once there, and they pull in opposite directions:
    /// </summary>
    /// <remarks>
    /// <para>
    /// One fragment gap must stay well inside this window, or a wait loop that DOES restart its
    /// window still looks like one that cut the reply short. That is the side this test kept
    /// failing on. The gap used to be counted in the reader thread's own idle reads —
    /// five <see cref="IdleReadSleepMs"/> sleeps — which made the gap the SUM of five scheduling
    /// delays rather than one. On the macOS runner each 10 ms sleep came back at roughly 100 ms
    /// under the load of the rest of the suite, so the nominal 50 ms gap arrived at ~500 ms, ate
    /// the whole window, and the exchange returned six of the twelve lines. Widening the window
    /// (300 ms -> 500 ms) did not fix it, because the stretch scales with the window.
    /// </para>
    /// <para>
    /// The gap is now measured on the stream's own clock — the next fragment is handed over on the
    /// first read that finds <see cref="StageGapMs"/> elapsed since the previous one drained — so a
    /// stretched sleep no longer accumulates. The gap is <see cref="StageGapMs"/> plus at most ONE
    /// late read, which means a single scheduling stall of 450 ms has to land inside one gap before
    /// the test lies about the exchange, instead of five stalls of 90 ms.
    /// </para>
    /// <para>
    /// The fragments must ALSO span longer than this window in total, or the test passes for a loop
    /// that never restarts anything. That needs
    /// (<see cref="StagedFragmentCount"/> - 1) x <see cref="StageGapMs"/> &gt; the window:
    /// 15 x 50 ms = 750 ms nominal, and more on a slow machine, since delay can only lengthen a
    /// gap. Both sides therefore hold in the direction a loaded runner pushes them — but the span
    /// is asserted from the stream's record of it rather than left to this arithmetic, so pacing
    /// that collapses fails instead of going vacuous.
    /// </para>
    /// </remarks>
    private const int StagedCompletionTimeoutMs = 500;

    /// <summary>
    /// Nominal gap between fragments, measured on the stream's clock rather than counted in idle
    /// reads; see <see cref="StagedCompletionTimeoutMs"/>.
    /// </summary>
    private const int StageGapMs = 50;

    /// <summary>
    /// How many fragments the staged reply is dribbled out in. Enough that the fragments outlast
    /// one completion window even though each gap is a small fraction of it.
    /// </summary>
    private const int StagedFragmentCount = 16;

    /// <summary>
    /// How long an idle read parks the consumer thread for. Only the polling interval now: it sets
    /// how soon after <see cref="StageGapMs"/> elapses the next fragment is noticed, and no longer
    /// multiplies into the gap itself.
    /// </summary>
    private const int IdleReadSleepMs = 10;

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceAnswersWithABareLineFeed_ReturnsTheLine()
    {
        // The firmware's ack for SYSTem:LOG:CLEar, byte for byte. A CRLF-only parser never finds a
        // terminator, so before the fix this line was not merely late — it was lost entirely, and
        // the caller got an empty response after the full timeout.
        using var transport = new ScriptedReplyTransport("Log cleared\n");
        using var device = new LineFramingTestableDevice("Bare LF Device", transport);

        device.Connect();

        var lines = await device.CallAsync(() => transport.Release());

        Assert.Equal(new[] { "Log cleared" }, lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceAnswersWithABareLineFeed_DoesNotWaitOutTheTimeout()
    {
        // The half of #538 that is about time rather than content: the reply arrives immediately,
        // so the exchange must finish in roughly the completion timeout, not the response timeout.
        using var transport = new ScriptedReplyTransport("Added test log messages\n");
        using var device = new LineFramingTestableDevice("Prompt Ack Device", transport);

        device.Connect();

        var lines = await device.CallAsync(() => transport.Release());

        Assert.Equal(new[] { "Added test log messages" }, lines);
        AssertRecognisedTheReply(device);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceAnswersWithOnlyABlankLine_DoesNotWaitOutTheTimeout()
    {
        // An empty SYSTem:LOG? answers with a lone CRLF and nothing else — measured on the bench,
        // three trials, every one 2 bytes in 6 ms. It carries no content, but it is proof that the
        // device answered, and treating it as silence cost the caller the full 2 s response
        // timeout on what is the normal case for a healthy device.
        using var transport = new ScriptedReplyTransport("\r\n");
        using var device = new LineFramingTestableDevice("Empty Log Device", transport);

        device.Connect();

        var lines = await device.CallAsync(() => transport.Release());

        AssertRecognisedTheReply(device);

        // ...and the blank line stays out of the caller's result. It is evidence for the wait
        // loop, not content: every caller of this seam parses lines, and none of them ever saw a
        // blank one before.
        Assert.Empty(lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceSaysNothingAtAll_StillWaitsForTheFullTimeout()
    {
        // The control that keeps the fix honest. Counting blank lines as an answer must not turn
        // an unresponsive device into one that answered quickly — a silent link is still a silent
        // link, and the caller is entitled to the full first-response window before it gives up.
        using var transport = new ScriptedReplyTransport("\r\n");
        using var device = new LineFramingTestableDevice("Silent Device", transport);

        device.Connect();

        var stopwatch = Stopwatch.StartNew();
        var lines = await device.CallAsync(() => { /* never released: nothing reaches the stream */ });
        stopwatch.Stop();

        Assert.Empty(lines);

        // The mirror of AssertRecognisedTheReply, and the half of this control that no longer
        // depends on the clock: silence must leave the exchange with nothing it would keep as an
        // answer. If counting blank lines ever started counting a device that sent none, this
        // flips to true here while the elapsed-time check below stays happily green.
        Assert.False(
            device.RecognisedTheReply,
            "A silent device left the exchange believing it had been answered.");

        Assert.True(
            stopwatch.ElapsedMilliseconds >= ResponseTimeoutMs - 500,
            $"A silent device should have used the whole {ResponseTimeoutMs}ms response window, "
            + $"but the exchange gave up after {stopwatch.ElapsedMilliseconds}ms.");

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceAnswersWithCarriageReturnLineFeed_IsUnchanged()
    {
        // The shape almost every reply uses. This is the check that actually matters, because the
        // risk of splitting on LF instead of CRLF is that the carriage return leaks into the line.
        using var transport = new ScriptedReplyTransport("0,\"No error\"\r\n");
        using var device = new LineFramingTestableDevice("CRLF Device", transport);

        device.Connect();

        var lines = await device.CallAsync(() => transport.Release());

        Assert.Equal(new[] { "0,\"No error\"" }, lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheReplyMixesBothLineEndings_ReturnsEveryLineInOrder()
    {
        // A multi-line dump terminated by the firmware's trailing blank line, with a bare-LF ack
        // in the middle for good measure. The blank is consumed by the exchange; everything else
        // reaches the caller in the order the device sent it.
        using var transport = new ScriptedReplyTransport(
            "Sample queue resize skipped\r\nLog cleared\ndiag: mask=0x0001\r\n\r\n");
        using var device = new LineFramingTestableDevice("Mixed Device", transport);

        device.Connect();

        var lines = await device.CallAsync(() => transport.Release());

        Assert.Equal(
            new[] { "Sample queue resize skipped", "Log cleared", "diag: mask=0x0001" },
            lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheDeviceAnswersInFragmentsSpacedApart_ReturnsEveryLine()
    {
        // The property the inactivity tail exists for, and the one an event-driven wait loop is
        // most likely to get wrong: every line must restart the completion window, so a device
        // that dribbles its answer out over more than one completion timeout in total is never cut
        // off part-way. A loop that instead measured its deadline from when it started waiting
        // would return the first few lines here and drop the rest.
        //
        // The fragments are paced on the stream's own clock — the next one is handed over on the
        // first read that finds StageGapMs elapsed since the previous one drained — rather than by
        // a delay on the test thread or by counting the reader thread's sleeps. A sleep is a floor,
        // not a promise, and a loaded runner stretches one; timing the gap instead of counting
        // sleeps keeps one stretched sleep from being multiplied into the whole gap. The margin
        // between a gap and the completion window is what keeps this honest, and it is sized in the
        // note on StagedCompletionTimeoutMs.
        var expected = Enumerable.Range(1, StagedFragmentCount).Select(i => $"line{i}").ToArray();

        using var transport = new ScriptedReplyTransport(
            stageGapMs: StageGapMs,
            expected.Select(line => $"{line}\r\n").ToArray());
        using var device = new LineFramingTestableDevice("Fragmented Device", transport);

        device.Connect();

        var lines = await device.CallAsync(
            () => transport.Release(),
            completionTimeoutMs: StagedCompletionTimeoutMs);

        Assert.Equal(expected, lines);
        AssertRecognisedTheReply(device);

        // Without this the test would still pass if the pacing ever collapsed to nothing — every
        // line would arrive inside a single completion window and a wait loop that never restarted
        // its window would look correct. Asserting the reply really did outlast one window is what
        // makes the collection assertion above evidence for the property in the comment.
        //
        // Asked of the stream rather than of a stopwatch around the call: the call cannot return
        // until a whole completion window has passed with no new line, so its own elapsed time is
        // greater than the window whatever the pacing did, and asserting on it proves nothing. The
        // span between the first and last fragment reaching the reader is the quantity that has to
        // outlast the window, and only the stream can measure it.
        var stagedSpanMs = transport.StagedSpan.TotalMilliseconds;
        Assert.True(stagedSpanMs > StagedCompletionTimeoutMs,
            $"The staged reply was handed over across {stagedSpanMs:F0} ms, which is inside one " +
            $"{StagedCompletionTimeoutMs} ms completion window: the fragments were not spaced apart, " +
            "so this test no longer exercises the window restarting on each line.");

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheCallerCancelsWhileWaitingForAReply_ThrowsOperationCanceled()
    {
        // The wait for the device's reply is cancellable, and must stay cancellable however it is
        // implemented. A silent device would otherwise hold the operation lock for the whole
        // first-response window with nothing the caller could do about it.
        using var transport = new ScriptedReplyTransport("\r\n");
        using var device = new LineFramingTestableDevice("Cancelled Device", transport);

        device.Connect();

        using var cancellation = new CancellationTokenSource();

        // Armed from inside the setup action so it fires while the exchange is waiting for a reply
        // that is never released, rather than racing the validation that runs before it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.CallAsync(
                () => cancellation.CancelAfter(50),
                cancellationToken: cancellation.Token));

        device.Disconnect();
    }

    private static void AssertRecognisedTheReply(LineFramingTestableDevice device)
    {
        // The exchange reports which branch its reply wait loop left by, so this asks it rather
        // than inferring the answer from a stopwatch.
        //
        // The bound this replaces was `elapsed < ResponseTimeoutMs - 1000`, i.e. "it finished well
        // inside the response timeout, so it must have recognised the reply". That inference
        // measures the runner as much as the exchange: the wait loop polls with
        // `await Task.Delay(50)`, and on a thread-pool-starved CI agent each of those continuations
        // can take a second to be scheduled — so an exchange that recognised the reply on its very
        // first poll still returns seconds later, and the stopwatch calls a fast path a timed-out
        // one. That is issue #634: 2129ms observed against a 2000ms bound, on a run where the
        // reply was recognised correctly the whole time, and green again on a re-run of the same
        // SHA. Reproduced here 10/10 under a starved pool, at 2.1-2.7s.
        //
        // Nothing is weakened by asking instead. The failure this guards against — a reply shape
        // the line framing cannot see, which is the whole of #538 — leaves the loop in its
        // first-response phase, so it still lands here as false. So does the #592 regression of a
        // reply that arrived before the loop's first poll going unnoticed. What the stopwatch
        // added on top of that was the machine's load, and that is all this drops.
        Assert.True(
            device.RecognisedTheReply,
            "The exchange never saw the reply as an answer, so it waited out its "
            + $"{ResponseTimeoutMs}ms first-response timeout instead of recognising it.");
    }

    /// <summary>Exposes the protected text-exchange entry point with the timeouts these tests need.</summary>
    private sealed class LineFramingTestableDevice : DaqifiDevice
    {
        public LineFramingTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        /// <summary>
        /// What the last exchange's reply wait loop concluded: <c>true</c> if it found evidence the
        /// device answered, <c>false</c> if it sat out its whole first-response timeout, and
        /// <c>null</c> if no exchange has finished waiting yet — which is itself a failure for
        /// every caller of it here, and reads as one.
        /// </summary>
        public bool? RecognisedTheReply { get; private set; }

        /// <summary>
        /// Records <see cref="DaqifiDevice.OnReplyWaitCompleted"/> so a test can assert on the
        /// branch the exchange took rather than on how long it took to get there. Written on the
        /// exchange's own task and read only after awaiting it, so the await orders the two.
        /// </summary>
        internal override void OnReplyWaitCompleted(bool sawResponse) => RecognisedTheReply = sawResponse;

        public Task<IReadOnlyList<string>> CallAsync(
            Action setupAction,
            int? completionTimeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: ResponseTimeoutMs,
                completionTimeoutMs: completionTimeoutMs ?? CompletionTimeoutMs,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Transport whose stream withholds one canned reply — raw bytes, so the line endings under
    /// test survive — until the setup action releases it.
    /// </summary>
    private sealed class ScriptedReplyTransport : IStreamTransport
    {
        private readonly ScriptedReplyStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public ScriptedReplyTransport(string reply)
            : this(stageGapMs: int.MaxValue, reply)
        {
        }

        /// <summary>
        /// A reply the device sends in fragments: each stage is handed over on the first read that
        /// finds <paramref name="stageGapMs"/> elapsed since the previous stage drained, so the gap
        /// between fragments is measured on the stream's clock rather than counted in the reader
        /// thread's sleeps — one late read can lengthen a gap, but it cannot be multiplied into it.
        /// </summary>
        public ScriptedReplyTransport(int stageGapMs, params string[] stages)
        {
            _stream = new ScriptedReplyStream(stages, stageGapMs);
        }

        /// <summary>
        /// How long the stream took to hand over every stage: the elapsed time from the first
        /// fragment draining to the last one draining, or <see cref="TimeSpan.Zero"/> if fewer than
        /// two fragments were ever handed over. This is what the fragmented-reply test asserts is
        /// longer than one completion window.
        /// </summary>
        public TimeSpan StagedSpan => _stream.StagedSpan;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(ScriptedReplyTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Scripted: Connected" : "Scripted: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Lets the canned reply reach the stream, as a device answering would.</summary>
        public void Release() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptedReplyTransport));
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

        private sealed class ScriptedReplyStream : Stream
        {
            private readonly byte[][] _stages;
            private readonly TimeSpan _stageGap;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly object _gate = new();
            private bool _released;
            private int _stage;
            private int _position;
            private TimeSpan _stageDrainedAt;
            private TimeSpan _firstStageDrainedAt;
            private TimeSpan _lastStageDrainedAt;
            private bool _anyStageDrained;

            public ScriptedReplyStream(string[] stages, int stageGapMs)
            {
                _stages = new byte[stages.Length][];
                for (var i = 0; i < stages.Length; i++)
                {
                    _stages[i] = Encoding.ASCII.GetBytes(stages[i]);
                }

                _stageGap = TimeSpan.FromMilliseconds(stageGapMs);
            }

            /// <summary>See <see cref="ScriptedReplyTransport.StagedSpan"/>.</summary>
            public TimeSpan StagedSpan
            {
                get
                {
                    lock (_gate)
                    {
                        return _lastStageDrainedAt - _firstStageDrainedAt;
                    }
                }
            }

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
                    if (_released && _stage < _stages.Length)
                    {
                        var current = _stages[_stage];
                        if (_position < current.Length)
                        {
                            var toCopy = Math.Min(count, current.Length - _position);
                            Array.Copy(current, _position, buffer, offset, toCopy);
                            _position += toCopy;

                            if (_position == current.Length)
                            {
                                // Handed over in full, so the gap to the next one starts here. The
                                // same instant is the one the reader turns into a line, which is
                                // what makes this the right clock to pace against.
                                _stageDrainedAt = _clock.Elapsed;
                                if (!_anyStageDrained)
                                {
                                    _anyStageDrained = true;
                                    _firstStageDrainedAt = _stageDrainedAt;
                                }

                                _lastStageDrainedAt = _stageDrainedAt;
                            }

                            return toCopy;
                        }

                        // This stage is drained. The next one is due once the gap has really
                        // elapsed on this clock — not once some number of sleeps have returned, a
                        // count that turns each stretched sleep on a loaded runner into a
                        // proportionally stretched gap.
                        if (_clock.Elapsed - _stageDrainedAt >= _stageGap)
                        {
                            _stage++;
                            _position = 0;
                        }
                    }
                }

                // Idle link: nothing to hand over, and no busy-spin in the reader thread.
                Thread.Sleep(IdleReadSleepMs);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
