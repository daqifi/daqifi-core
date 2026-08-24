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

        public Task<IReadOnlyList<string>> CallAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: ResponseTimeoutMs,
                completionTimeoutMs: CompletionTimeoutMs);
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
        {
            _stream = new ScriptedReplyStream(reply);
        }

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
            private readonly byte[] _reply;
            private readonly object _gate = new();
            private bool _released;
            private int _position;

            public ScriptedReplyStream(string reply) => _reply = Encoding.ASCII.GetBytes(reply);

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
                    if (_released && _position < _reply.Length)
                    {
                        var toCopy = Math.Min(count, _reply.Length - _position);
                        Array.Copy(_reply, _position, buffer, offset, toCopy);
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
}
