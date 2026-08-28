using System.Text;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Covers <see cref="ProtobufMessageParser.TryReadFrame"/>, the stepping entry point added for
/// issue #697 so <c>SdCardFileParser</c> can take one frame at a time instead of paying for a
/// whole 64 KB read buffer before its first sample.
/// </summary>
/// <remarks>
/// Stepping and <see cref="ProtobufMessageParser.ParseMessages(byte[], out int)"/> run the same
/// frame-boundary code, so the risk this file guards is that they stop doing so: the resync,
/// gap-gate and leading-noise rules are subtle and were arrived at through four separate field
/// failures (#268 among them). The equivalence theory below is therefore the important test —
/// it re-runs the whole existing corpus of awkward buffers through the stepping API and demands
/// the same frames and the same consumed count.
/// </remarks>
public class ProtobufMessageParserSteppingTests
{
    [Fact]
    public void TryReadFrame_WithMultipleFrames_ReturnsOneFramePerStep()
    {
        // The property the SD card parser depends on: asking for a frame decodes one frame,
        // not every frame in the buffer. Before #697 the only entry point parsed the lot.
        var parser = new ProtobufMessageParser();
        var data = Frames(7, 9, 11);

        var offset = 0;
        var timestamps = new List<uint>();

        for (var i = 0; i < 3; i++)
        {
            var step = parser.TryReadFrame(data, offset, timestamps.Count > 0, out var frame, out var nextOffset);

            Assert.Equal(ProtobufFrameStep.Frame, step);
            Assert.NotNull(frame);
            Assert.True(nextOffset > offset, "A decoded frame must advance the offset.");

            timestamps.Add(frame!.MsgTimeStamp);
            offset = nextOffset;
        }

        Assert.Equal(new uint[] { 7, 9, 11 }, timestamps);

        // Exhausted, and the whole buffer was consumed.
        var end = parser.TryReadFrame(data, offset, anyFrameRead: true, out var none, out var endOffset);
        Assert.Equal(ProtobufFrameStep.EndOfBuffer, end);
        Assert.Null(none);
        Assert.Equal(offset, endOffset);
        Assert.Equal(data.Length, offset);
    }

    [Fact]
    public void TryReadFrame_StoppingEarly_DecodesNothingBeyondTheFrameItWasAskedFor()
    {
        // The stepper is only useful if abandoning it costs nothing further. A buffer whose
        // tail is unparseable garbage still hands back the leading frame, because that tail is
        // never looked at: had the parser decoded ahead, the garbage would be in its way.
        var parser = new ProtobufMessageParser();
        var data = Frames(42).Concat(Enumerable.Repeat((byte)0xFF, 4096)).ToArray();

        var step = parser.TryReadFrame(data, 0, anyFrameRead: false, out var frame, out _);

        Assert.Equal(ProtobufFrameStep.Frame, step);
        Assert.Equal(42u, frame!.MsgTimeStamp);
    }

    [Fact]
    public void TryReadFrame_WithTrailingPartialFrame_PreservesItInsteadOfAdvancing()
    {
        // A frame split across reads must be left for the caller to complete, so EndOfBuffer
        // reports the offset unchanged — that is what makes nextOffset double as the consumed
        // count for the caller's carry-over buffer.
        var parser = new ProtobufMessageParser();
        var whole = Frames(7, 9);

        // One byte short, so the second frame is genuinely incomplete rather than absent.
        var truncated = whole[..^1];

        var first = parser.TryReadFrame(truncated, 0, anyFrameRead: false, out _, out var offset);
        Assert.Equal(ProtobufFrameStep.Frame, first);

        var step = parser.TryReadFrame(truncated, offset, anyFrameRead: true, out var frame, out var nextOffset);

        Assert.Equal(ProtobufFrameStep.EndOfBuffer, step);
        Assert.Null(frame);
        Assert.Equal(offset, nextOffset);
        Assert.True(offset < truncated.Length, "The partial frame's bytes must be left unconsumed.");
    }

    [Fact]
    public void TryReadFrame_AnyFrameRead_GatesLeadingNoiseRecovery()
    {
        // anyFrameRead is not cosmetic: it selects whether the expensive scan-ahead for a real
        // frame behind leading noise runs (#268). A caller stepping through one buffer must
        // therefore pass "have I taken a frame from THIS buffer", and reset per buffer — which
        // is why SdCardFileParser counts frames per chunk rather than for the whole file.
        var parser = new ProtobufMessageParser();
        var data = Encoding.ASCII.GetBytes("SYSTem:SYSInfoPB?\r\n").Concat(Frames(42)).ToArray();

        // No frame taken yet: the noise is recognized and skipped.
        var recovering = parser.TryReadFrame(data, 0, anyFrameRead: false, out _, out var recoveredOffset);
        Assert.Equal(ProtobufFrameStep.Resync, recovering);
        Assert.True(recoveredOffset > 0);

        // Told a frame was already taken, the same position reads as a partial frame still
        // arriving, and is preserved rather than skipped.
        var waiting = parser.TryReadFrame(data, 0, anyFrameRead: true, out var frame, out var waitingOffset);
        Assert.Equal(ProtobufFrameStep.EndOfBuffer, waiting);
        Assert.Null(frame);
        Assert.Equal(0, waitingOffset);
    }

    [Theory]
    [MemberData(nameof(AwkwardBuffers))]
    public void TryReadFrame_SteppedToExhaustion_AgreesWithParseMessages(string because, byte[] data)
    {
        // ParseMessages is itself a loop over the stepper, so this pins that the two stay one
        // implementation — including for the buffers that motivated the resync rules.
        var expected = new ProtobufMessageParser().ParseMessages(data, out var expectedConsumed);
        var expectedTimestamps = expected.Select(m => m.Data.MsgTimeStamp).ToArray();

        var parser = new ProtobufMessageParser();
        var actualTimestamps = new List<uint>();
        var offset = 0;
        var consumed = 0;

        while (true)
        {
            var step = parser.TryReadFrame(data, offset, actualTimestamps.Count > 0, out var frame, out var nextOffset);
            if (step == ProtobufFrameStep.EndOfBuffer)
            {
                break;
            }

            offset = nextOffset;
            consumed = nextOffset;

            if (step == ProtobufFrameStep.Frame)
            {
                actualTimestamps.Add(frame!.MsgTimeStamp);
            }
        }

        Assert.True(
            expectedTimestamps.SequenceEqual(actualTimestamps),
            $"Stepping disagreed with ParseMessages on the frames of {because}: " +
            $"expected [{string.Join(", ", expectedTimestamps)}], got [{string.Join(", ", actualTimestamps)}].");
        Assert.True(
            expectedConsumed == consumed,
            $"Stepping disagreed with ParseMessages on the consumed byte count of {because}: " +
            $"expected {expectedConsumed}, got {consumed}.");
    }

    public static TheoryData<string, byte[]> AwkwardBuffers()
    {
        var frame = Frames(42);

        return new TheoryData<string, byte[]>
        {
            { "empty buffer", [] },
            { "a single clean frame", frame },
            { "several clean frames", Frames(7, 9, 11) },
            { "invalid data only", [0xFF, 0xFF, 0xFF, 0xFF] },
            { "null-byte run then a frame", Enumerable.Repeat((byte)0x00, 20).Concat(frame).ToArray() },
            { "a frame truncated mid-body", frame[..^1] },
            { "a length prefix and nothing else", [frame[0]] },
            {
                "echoed command text wrapping a frame (#268)",
                Encoding.ASCII.GetBytes("SYSTem:SYSInfoPB?\r\n")
                    .Concat(frame)
                    .Concat(Encoding.ASCII.GetBytes("\r\nDAQIFI>"))
                    .ToArray()
            },
            { "a complete frame followed by a partial one", Frames(7).Concat(Frames(9)[..^2]).ToArray() },
            { "garbage claiming a multi-KB body", new byte[] { 0xFF, 0xFF, 0x03, 0x08 } },
            { "trailing garbage after a good frame", frame.Concat(Enumerable.Repeat((byte)0xFF, 64)).ToArray() },
        };
    }

    /// <summary>
    /// Builds a buffer of length-delimited frames, one per supplied timestamp.
    /// </summary>
    private static byte[] Frames(params uint[] timestamps)
    {
        using var stream = new MemoryStream();
        foreach (var timestamp in timestamps)
        {
            new DaqifiOutMessage { MsgTimeStamp = timestamp }.WriteDelimitedTo(stream);
        }

        return stream.ToArray();
    }
}
