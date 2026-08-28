using BenchmarkDotNet.Attributes;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// The consumer's own loop: read from the stream, append to the accumulation buffer, parse out
/// whatever is complete, keep the remainder, dispatch. This is where the cost issue #490 attacked
/// actually lives — the buffer copy per read, and the byte-at-a-time append that preceded it.
/// </summary>
/// <remarks>
/// <para>
/// The scripted stream hands out 511 bytes per read, which is deliberately not a multiple of the
/// frame size, so nearly every read ends mid-frame and the partial-frame path runs constantly
/// rather than once at the end.
/// </para>
/// <para>
/// <b>What the measured window covers.</b> Only <see cref="IMessageConsumer{T}.Start"/> and the
/// drain: the consumer, the stream and the subscriber are built in
/// <see cref="IterationSetup"/> and torn down in <see cref="IterationCleanup"/>, neither of which
/// BenchmarkDotNet measures. That is not tidiness. <c>Stop()</c> joins the reader thread, and by
/// the time the last frame has been dispatched that thread has gone back for one more read, found
/// the stream empty, and entered its 10 ms no-data backoff — so a <c>Stop()</c> inside the
/// measured window contributes a flat ~10 ms that has nothing to do with framing. Measured that
/// way this case read 43x the per-frame cost of the parser it wraps, essentially all of it the
/// backoff.
/// </para>
/// <para>
/// <b>What it costs that Core's own path does not.</b> The subscriber here attaches to the public
/// <see cref="IMessageConsumer{T}.MessageReceived"/>, because that is the only completion signal a
/// benchmark can see; Core's internals listen on the internal <c>MessageParsed</c> instead. The
/// difference is not free — with a <c>MessageReceived</c> subscriber attached, every read
/// snapshots the whole accumulation buffer into a fresh array for the event's <c>RawData</c>. So
/// read this case as the cost to an application that subscribes, which is a real configuration,
/// and not as what Core pays internally.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StreamConsumerBenchmarks
{
    /// <summary>
    /// Frames fed to the consumer per iteration. Sized so one iteration is tens of milliseconds of
    /// real work: the thread start inside the measured window is then noise rather than the
    /// measurement.
    /// </summary>
    private const int FrameCount = 100_000;

    /// <summary>
    /// Bytes handed out per read, chosen to be a non-multiple of the frame size so reads land
    /// mid-frame the way a real USB read does.
    /// </summary>
    private const int ChunkSize = 511;

    private byte[] _frames = null!;

    private ChunkedStream _stream = null!;
    private CountdownEvent _received = null!;
    private StreamMessageConsumer<DaqifiOutMessage> _consumer = null!;

    [GlobalSetup]
    public void Setup() => _frames = SyntheticFrames.BuildFrameBuffer(FrameCount);

    [IterationSetup]
    public void IterationSetup()
    {
        _stream = new ChunkedStream(_frames, ChunkSize);
        _received = new CountdownEvent(FrameCount);
        _consumer = new StreamMessageConsumer<DaqifiOutMessage>(_stream, new ProtobufMessageParser());
        _consumer.MessageReceived += OnMessageReceived;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _consumer.MessageReceived -= OnMessageReceived;
        _consumer.Dispose();
        _received.Dispose();
        _stream.Dispose();
    }

    /// <summary>
    /// Start the reader and wait for every frame to come back out.
    /// </summary>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public void ConsumeScriptedStream()
    {
        _consumer.Start();

        // Generous by two orders of magnitude: the work is tens of milliseconds. A timeout here
        // means the benchmark would be measuring a stall, and reporting that as a number would be
        // worse than failing.
        if (!_received.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException(
                $"Only {FrameCount - _received.CurrentCount} of {FrameCount} frames were consumed.");
        }
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs<DaqifiOutMessage> e)
    {
        // Guarded because the countdown must not be signalled past zero: the consumer dispatches
        // on one thread, but a frame arriving after the last Signal would still throw.
        if (!_received.IsSet)
        {
            _received.Signal();
        }
    }

    /// <summary>
    /// A read-only stream that hands out at most <c>chunkSize</c> bytes per read and then returns
    /// zero, which the consumer treats as "nothing right now" rather than end-of-stream. Standing
    /// in for a serial port; the point is that reads land mid-frame.
    /// </summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
