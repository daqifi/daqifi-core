using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Tests.TestSupport;
using Google.Protobuf;
using System.Collections.Concurrent;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Coverage for <see cref="StreamMessageConsumer{T}.Reframe"/>, which lets the device keep one
/// reader for both its protobuf frames and its text replies instead of swapping readers around
/// every text exchange (issue #900).
/// </summary>
public class StreamMessageConsumerReframeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void Reframe_DiscardsTheHalfFramedMessageAndFramesWhatFollowsTheNewWay()
    {
        // A protobuf frame the reader has begun and not finished when an exchange switches it to
        // text. Kept, its bytes would be read as the start of the reply's first line — a binary
        // prefix glued onto "0,No error". Discarded at the switch, the reply arrives clean.
        using var stream = new PushStream();
        var parser = new DeviceWireParser();
        using var consumer = new StreamMessageConsumer<WireMessage>(stream, parser);
        var received = new ConcurrentQueue<WireMessage>();
        consumer.MessageParsed += m => received.Enqueue(m.Data);
        consumer.Start();

        var frame = Delimited(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 });
        var half = frame[..(frame.Length / 2)];
        Assert.DoesNotContain((byte)'\n', half);
        stream.Push(half);
        WaitUntil.That(() => stream.IsDrained && consumer.QueuedMessageCount > 0,
            "the reader to buffer the half frame", Budget);

        var capture = new TextReplyCapture(_ => { });
        consumer.Reframe(() => parser.Capture = capture);
        Assert.Equal(0, consumer.QueuedMessageCount);

        stream.Push(Encoding.ASCII.GetBytes("0,\"No error\"\r\n"));
        WaitUntil.That(() => !received.IsEmpty, "the reply line to be framed", Budget);

        var line = Assert.Single(received);
        Assert.Equal("0,\"No error\"", line.Line);
        Assert.Same(capture, line.Capture);
    }

    [Fact]
    public void Reframe_BackToProtobuf_DiscardsTheUnfinishedLineAndDecodesTheNextFrame()
    {
        using var stream = new PushStream();
        var parser = new DeviceWireParser { Capture = new TextReplyCapture(_ => { }) };
        using var consumer = new StreamMessageConsumer<WireMessage>(stream, parser);
        var received = new ConcurrentQueue<WireMessage>();
        consumer.MessageParsed += m => received.Enqueue(m.Data);
        consumer.Start();

        stream.Push(Encoding.ASCII.GetBytes("a reply the exchange stopped listening to"));
        WaitUntil.That(() => stream.IsDrained && consumer.QueuedMessageCount > 0,
            "the reader to buffer the unfinished line", Budget);

        consumer.Reframe(() => parser.Capture = null);
        stream.Push(Delimited(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 }));
        WaitUntil.That(() => !received.IsEmpty, "the status frame to be decoded", Budget);

        var message = Assert.Single(received);
        Assert.Equal("Nq1", message.Frame!.DevicePn);
    }

    [Fact]
    public async Task Reframe_WaitsForAParseInProgressRatherThanLandingInsideIt()
    {
        // The switch must fall between two batches. A parser that is part-way through a buffer
        // when the framing changes under it would frame one buffer two ways at once.
        using var stream = new PushStream();
        var parser = new GatedParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        consumer.Start();

        stream.Push(Encoding.ASCII.GetBytes("line\n"));
        Assert.True(parser.Entered.Wait(Budget), "the reader never began parsing");

        var applied = new ManualResetEventSlim(false);
        var reframe = Task.Run(() => consumer.Reframe(applied.Set));

        WaitUntil.HoldsFor(
            () => !applied.IsSet,
            "the framing changed while a parse was still running",
            TimeSpan.FromMilliseconds(200));

        parser.Release.Set();
        await reframe.WaitAsync(Budget);
        Assert.True(applied.IsSet);
    }

    [Fact]
    public void Reframe_OnAStoppedOrDisposedConsumer_StillAppliesAndDoesNotThrow()
    {
        using var stream = new PushStream();
        var consumer = new StreamMessageConsumer<WireMessage>(stream, new DeviceWireParser());

        var calls = 0;
        consumer.Reframe(() => calls++);

        consumer.Start();
        consumer.StopSafely();
        consumer.Reframe(() => calls++);

        consumer.Dispose();
        consumer.Reframe(() => calls++);

        Assert.Equal(3, calls);
        Assert.Throws<ArgumentNullException>(() => consumer.Reframe(null!));
    }

    [Fact]
    public async Task WaitForDispatchToFinish_WaitsForASubscriberStillHandlingTheLastBatch()
    {
        using var stream = new PushStream();
        var parser = new DeviceWireParser();
        using var consumer = new StreamMessageConsumer<WireMessage>(stream, parser);
        using var inHandler = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        consumer.MessageParsed += _ =>
        {
            inHandler.Set();
            release.Wait(Budget);
        };
        consumer.Start();

        Assert.True(await consumer.WaitForDispatchToFinishAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None), "an idle reader should not be waited for");

        stream.Push(Delimited(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 }));
        Assert.True(inHandler.Wait(Budget), "the subscriber never ran");

        Assert.False(await consumer.WaitForDispatchToFinishAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None), "the wait returned while the subscriber was still running");

        using var cts = new CancellationTokenSource();
        var cancelled = consumer.WaitForDispatchToFinishAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var waited = consumer.WaitForDispatchToFinishAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waited.IsCompleted);
        release.Set();
        Assert.True(await waited.WaitAsync(Budget));
    }

    [Fact]
    public async Task WaitForDispatchToFinish_OnTheReaderThread_ReturnsAtOnce()
    {
        // A subscriber that runs an exchange synchronously would otherwise wait on its own thread.
        using var stream = new PushStream();
        using var consumer = new StreamMessageConsumer<WireMessage>(stream, new DeviceWireParser());
        var result = new TaskCompletionSource<(bool Waited, long Ms)>();
        consumer.MessageParsed += _ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var waited = consumer.WaitForDispatchToFinishAsync(TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            result.TrySetResult((waited, sw.ElapsedMilliseconds));
        };
        consumer.Start();

        stream.Push(Delimited(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 }));
        var (waited, ms) = await result.Task.WaitAsync(Budget);
        Assert.True(waited);
        Assert.True(ms < 1000, $"waited {ms} ms on its own thread");
    }

    private static byte[] Delimited(DaqifiOutMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteDelimitedTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Frames lines, but blocks inside its first parse until released, so a test can hold the
    /// reader part-way through a batch.
    /// </summary>
    private sealed class GatedParser : IMessageParser<string>
    {
        private readonly LineBasedMessageParser _inner = new(lineEnding: "\n");
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
            => ParseMessages(new ReadOnlySpan<byte>(data), out consumedBytes);

        public IEnumerable<IInboundMessage<string>> ParseMessages(ReadOnlySpan<byte> data, out int consumedBytes)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait();
            }

            return _inner.ParseMessages(data, out consumedBytes);
        }
    }

    /// <summary>
    /// A stream whose reads return what the test has pushed, and otherwise time out quickly with
    /// nothing, the way a serial port with a short read timeout does.
    /// </summary>
    private sealed class PushStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new();
        private int _pending;

        public bool IsDrained => Volatile.Read(ref _pending) == 0;

        public void Push(byte[] chunk)
        {
            Interlocked.Increment(ref _pending);
            _chunks.Add(chunk);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_chunks.TryTake(out var chunk, 10))
            {
                return 0;
            }

            // Every test chunk is far smaller than the consumer's 4 KB read buffer.
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            Interlocked.Decrement(ref _pending);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunks.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
