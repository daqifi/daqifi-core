using BenchmarkDotNet.Attributes;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// Everything between bytes arriving on the wire and a <see cref="DaqifiOutMessage"/> existing:
/// the varint length prefix, the frame boundary, and the buffer the consumer accumulates into.
/// </summary>
/// <remarks>
/// <para>
/// Two cases, and the difference between them is the point.
/// <see cref="ParseWholeFrames"/> is the tidy case — a buffer that happens to end on a frame
/// boundary. <see cref="ParseWithTrailingPartialFrame"/> is the far commoner one: a USB read
/// almost never ends where a frame does, so the last bytes are a partial the parser must decline,
/// leave unconsumed, and re-examine on the next read.
/// </para>
/// <para>
/// Both pass the same buffer every iteration and are pure with respect to it, so the allocation
/// column is the frames themselves plus whatever the framing adds on top. The consumer loop that
/// feeds this parser is measured separately in <see cref="StreamConsumerBenchmarks"/>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ProtobufFramingBenchmarks
{
    /// <summary>
    /// Frames per buffer. A 16-channel raw-count frame is about 40 bytes on the wire, so a 4 KB
    /// read off a USB CDC port holds roughly this many.
    /// </summary>
    private const int FrameCount = 100;

    private readonly ProtobufMessageParser _parser = new();

    private byte[] _wholeFrames = null!;
    private byte[] _framesWithTrailingPartial = null!;

    [GlobalSetup]
    public void Setup()
    {
        _wholeFrames = SyntheticFrames.BuildFrameBuffer(FrameCount);

        // FrameCount + 1 frames with the last one truncated, so both cases yield exactly
        // FrameCount complete frames and OperationsPerInvoke means the same thing in each.
        _framesWithTrailingPartial = SyntheticFrames.BuildFrameBuffer(FrameCount + 1, truncateLastFrame: true);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = FrameCount)]
    public int ParseWholeFrames()
    {
        var messages = _parser.ParseMessages(_wholeFrames, out _);
        return Count(messages);
    }

    [Benchmark(OperationsPerInvoke = FrameCount)]
    public int ParseWithTrailingPartialFrame()
    {
        var messages = _parser.ParseMessages(_framesWithTrailingPartial, out _);
        return Count(messages);
    }

    private static int Count(IEnumerable<IInboundMessage<DaqifiOutMessage>> messages)
    {
        var count = 0;
        foreach (var _ in messages)
        {
            count++;
        }

        return count;
    }
}
