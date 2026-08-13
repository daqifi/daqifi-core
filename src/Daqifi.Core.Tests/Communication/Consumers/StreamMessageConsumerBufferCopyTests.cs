using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Tests for what <see cref="StreamMessageConsumer{T}"/> copies on every read (issue #490).
/// </summary>
/// <remarks>
/// Two copies used to happen per read regardless of what anyone needed: the accumulation buffer was
/// filled a byte at a time from the read buffer, and then the whole accumulation buffer was copied
/// out again purely so the parser could be handed an array. On a device streaming at kilohertz
/// rates that is the wire throughput duplicated as garbage, for the life of the connection. What is
/// asserted here is the observable consequence — the parser is handed a view rather than a copy,
/// and the raw-buffer snapshot is taken only for a subscriber that asked for it — plus the framing
/// behaviour that must survive the change.
/// </remarks>
public class StreamMessageConsumerBufferCopyTests
{
    /// <summary>
    /// The parser now sees the span entry point. That is the whole mechanism by which the per-read
    /// copy disappears, so it is asserted directly rather than inferred.
    /// </summary>
    [Fact]
    public void TheParserIsCalledThroughItsSpanEntryPoint()
    {
        var parser = new EntryPointRecordingParser();
        using var stream = new MemoryStream(Frame(1));
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        var received = new ManualResetEventSlim(false);
        consumer.MessageParsed += _ => received.Set();

        consumer.Start();
        var fired = received.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired, "the consumer should have parsed the frame");
        Assert.True(parser.SpanCalls > 0);
        Assert.Equal(0, parser.ArrayCalls);
    }

    /// <summary>
    /// The raw-buffer snapshot exists for <c>MessageReceived</c> subscribers. With none attached
    /// there is nobody to hand it to, so it is not taken — this is the allocation that used to
    /// happen on every read of every stream whether or not anything wanted it.
    /// </summary>
    [Fact]
    public void WithNoMessageReceivedSubscriber_NoBufferSnapshotIsTaken()
    {
        using var stream = new MemoryStream(Frame(1));
        using var consumer = new RawDataRecordingConsumer(stream);

        var received = new ManualResetEventSlim(false);
        consumer.MessageParsed += _ => received.Set();

        consumer.Start();
        var fired = received.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired);
        Assert.NotNull(consumer.LastRawData);
        Assert.Empty(consumer.LastRawData!);
    }

    /// <summary>
    /// And with a subscriber attached it keeps its documented meaning: everything that was buffered
    /// when the batch was parsed, handed over before the consumed bytes are drained.
    /// </summary>
    [Fact]
    public void WithAMessageReceivedSubscriber_TheSnapshotIsStillTheWholeBufferedRead()
    {
        var frame = Frame(1);
        using var stream = new MemoryStream(frame);
        using var consumer = new RawDataRecordingConsumer(stream);

        var received = new ManualResetEventSlim(false);
        byte[]? rawFromArgs = null;
        consumer.MessageReceived += (_, args) =>
        {
            rawFromArgs = args.RawData;
            received.Set();
        };

        consumer.Start();
        var fired = received.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired);
        Assert.Equal(frame, rawFromArgs);
        Assert.Equal(frame, consumer.LastRawData);
    }

    /// <summary>
    /// Both events describe the same messages, in the same order. <see cref="StreamMessageConsumer{T}.MessageParsed"/>
    /// is only a cheaper view of <c>MessageReceived</c>, not a different feed.
    /// </summary>
    [Fact]
    public void BothEventsSeeTheSameMessagesInTheSameOrder()
    {
        var payload = Frame(1).Concat(Frame(2)).Concat(Frame(3)).ToArray();
        using var stream = new MemoryStream(payload);
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, new ProtobufMessageParser());

        var parsed = new List<uint>();
        var receivedList = new List<uint>();
        var done = new ManualResetEventSlim(false);

        consumer.MessageParsed += message => parsed.Add(message.Data.MsgTimeStamp);
        consumer.MessageReceived += (_, args) =>
        {
            receivedList.Add(args.Message.Data.MsgTimeStamp);
            if (receivedList.Count == 3)
            {
                done.Set();
            }
        };

        consumer.Start();
        var fired = done.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired);
        Assert.Equal(new uint[] { 1, 2, 3 }, parsed);
        Assert.Equal(new uint[] { 1, 2, 3 }, receivedList);
    }

    /// <summary>
    /// The bulk append has to accumulate across reads exactly as the byte-at-a-time loop did: a
    /// frame split over two reads must still be reassembled and parsed once.
    /// </summary>
    [Fact]
    public void AFrameSplitAcrossReads_IsStillReassembled()
    {
        var frame = Frame(4242);
        using var stream = new ChunkedStream(frame.Take(3).ToArray(), frame.Skip(3).ToArray());
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, new ProtobufMessageParser());

        var messages = new List<uint>();
        var received = new ManualResetEventSlim(false);
        consumer.MessageParsed += message =>
        {
            messages.Add(message.Data.MsgTimeStamp);
            received.Set();
        };

        consumer.Start();
        var fired = received.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired);
        Assert.Equal(new uint[] { 4242 }, messages);
    }

    /// <summary>
    /// The two events are independent deliveries. Core's own subscribers ride
    /// <c>MessageParsed</c>, so one of them throwing must not withhold the message from an external
    /// <c>MessageReceived</c> subscriber that had nothing to do with the failure — and the failure
    /// must still be reported.
    /// </summary>
    [Fact]
    public void AThrowingMessageParsedSubscriber_DoesNotWithholdMessageReceived()
    {
        using var stream = new MemoryStream(Frame(7).Concat(Frame(8)).ToArray());
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, new ProtobufMessageParser());

        var errors = new List<Exception>();
        var delivered = new List<uint>();
        var done = new ManualResetEventSlim(false);

        consumer.MessageParsed += _ => throw new InvalidOperationException("bad internal handler");
        consumer.ErrorOccurred += (_, e) => errors.Add(e.Error);
        consumer.MessageReceived += (_, args) =>
        {
            delivered.Add(args.Message.Data.MsgTimeStamp);
            if (delivered.Count == 2)
            {
                done.Set();
            }
        };

        consumer.Start();
        var fired = done.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        Assert.True(fired, "both messages should still have reached MessageReceived");
        Assert.Equal(new uint[] { 7, 8 }, delivered);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.IsType<InvalidOperationException>(error));
    }

    private static byte[] Frame(uint timestamp)
    {
        var message = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        using var buffer = new MemoryStream();
        message.WriteDelimitedTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Records which of the parser's two entry points the consumer used.
    /// </summary>
    private sealed class EntryPointRecordingParser : IMessageParser<DaqifiOutMessage>
    {
        private readonly ProtobufMessageParser _inner = new();

        public int ArrayCalls { get; private set; }

        public int SpanCalls { get; private set; }

        public IEnumerable<IInboundMessage<DaqifiOutMessage>> ParseMessages(byte[] data, out int consumedBytes)
        {
            ArrayCalls++;
            return _inner.ParseMessages(data, out consumedBytes);
        }

        public IEnumerable<IInboundMessage<DaqifiOutMessage>> ParseMessages(
            ReadOnlySpan<byte> data,
            out int consumedBytes)
        {
            SpanCalls++;
            return _inner.ParseMessages(data, out consumedBytes);
        }
    }

    /// <summary>
    /// Captures the raw-data argument the consumer passes down to <c>OnMessageReceived</c>, which
    /// is the only place the snapshot decision is observable.
    /// </summary>
    private sealed class RawDataRecordingConsumer : StreamMessageConsumer<DaqifiOutMessage>
    {
        public RawDataRecordingConsumer(Stream stream)
            : base(stream, new ProtobufMessageParser())
        {
        }

        public byte[]? LastRawData { get; private set; }

        protected override void OnMessageReceived(IInboundMessage<DaqifiOutMessage> message, byte[] rawData)
        {
            LastRawData = rawData;
            base.OnMessageReceived(message, rawData);
        }
    }

    /// <summary>
    /// Hands out its content one chunk per read, so a frame can be forced to arrive split.
    /// </summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;

        public ChunkedStream(params byte[][] chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
            {
                // Behaves like an idle serial port: no data right now, not end of stream.
                Thread.Sleep(5);
                return 0;
            }

            var chunk = _chunks.Dequeue();
            var length = Math.Min(chunk.Length, count);
            Array.Copy(chunk, 0, buffer, offset, length);
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
