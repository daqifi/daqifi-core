using System.Text;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// A status reply that arrives in two reads must come out whole, wherever the read boundary
/// falls (#926). The leading-noise recovery added for #268 scanned the first read for a
/// complete frame further along, and a large status frame carries complete sub-messages in
/// its own body: the scan found one, skipped to it, and the real reply was lost. On the bench
/// 564 of 571 split points of a captured reply lost it, and about 1 connect in 30 failed.
/// </summary>
public class ProtobufMessageParserSplitFrameTests
{
    private static byte[] Delimited(DaqifiOutMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteDelimitedTo(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Drives the parser the way <see cref="StreamMessageConsumer{T}"/> does: each read is
    /// appended to the buffer, the whole buffer is parsed, and the consumed prefix is trimmed.
    /// </summary>
    private static List<DaqifiOutMessage> ParseInReads(params byte[][] reads)
    {
        var parser = new ProtobufMessageParser();
        var buffer = new List<byte>();
        var parsed = new List<DaqifiOutMessage>();

        foreach (var read in reads)
        {
            buffer.AddRange(read);
            var messages = parser.ParseMessages(buffer.ToArray(), out var consumed).ToList();
            buffer.RemoveRange(0, Math.Min(consumed, buffer.Count));
            parsed.AddRange(messages.Select(m => Assert.IsType<DaqifiOutMessage>(m.Data)));
        }

        return parsed;
    }

    [Fact]
    public void StatusReplySplitAcrossTwoReads_ComesOutWholeAtEverySplitPoint()
    {
        var status = CompositeMessageParserBenchCaptureTests.BuildStatusMessage();
        var reply = Delimited(status).Concat(Encoding.ASCII.GetBytes("\r\n")).ToArray();

        var lost = new List<int>();
        for (var split = 1; split < reply.Length; split++)
        {
            var parsed = ParseInReads(reply[..split], reply[split..]);
            if (parsed.Count != 1 || !parsed[0].Equals(status))
            {
                lost.Add(split);
            }
        }

        Assert.True(
            lost.Count == 0,
            $"{lost.Count} of {reply.Length - 1} split points lost the reply, first at byte {lost.FirstOrDefault()}.");
    }

    [Fact]
    public void StatusReplySplitAcrossThreeReads_ComesOutWhole()
    {
        // The bench saw two reads; a slower host can see more. Every pair of boundaries a
        // tenth of the frame apart.
        var status = CompositeMessageParserBenchCaptureTests.BuildStatusMessage();
        var reply = Delimited(status);
        var step = Math.Max(1, reply.Length / 10);

        for (var first = 1; first < reply.Length; first += step)
        {
            for (var second = first + 1; second < reply.Length; second += step)
            {
                var parsed = ParseInReads(reply[..first], reply[first..second], reply[second..]);

                Assert.True(
                    parsed.Count == 1 && parsed[0].Equals(status),
                    $"Reads split at {first} and {second} lost the reply.");
            }
        }
    }

    [Fact]
    public void EchoedCommandAheadOfASplitStatusReply_ComesOutWholeAtEverySplitPointInsideTheFrame()
    {
        // #268 must keep working when the reply behind the echoed command arrives in two
        // reads, wherever inside the frame the boundary falls.
        var status = CompositeMessageParserBenchCaptureTests.BuildStatusMessage();
        var frame = Delimited(status);
        var echo = Encoding.ASCII.GetBytes("SYSTem:SYSInfoPB?\r\n");
        var data = echo.Concat(frame).Concat(Encoding.ASCII.GetBytes("\r\nDAQIFI>")).ToArray();

        var lost = new List<int>();
        for (var split = echo.Length + 1; split < echo.Length + frame.Length; split++)
        {
            var parsed = ParseInReads(data[..split], data[split..]);
            if (parsed.Count != 1 || !parsed[0].Equals(status))
            {
                lost.Add(split);
            }
        }

        Assert.True(
            lost.Count == 0,
            $"{lost.Count} of {frame.Length - 1} split points lost the reply, first at byte {lost.FirstOrDefault()}.");
    }

    [Fact]
    public void EchoedCommandThatReadsAsProtobufFields_IsStillSkipped()
    {
        // The echo can happen to read as a run of well-formed protobuf fields for the length its
        // first byte declares. It is skipped as a line of text, so the complete reply behind it
        // is read rather than waited on.
        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceSn = 7,
            AnalogInPortNum = 16,
            HostName = "xx",
        };
        var data = Encoding.ASCII.GetBytes("SYSTem:SYSInfoPB?\r\n")
            .Concat(Delimited(status))
            .Concat(Encoding.ASCII.GetBytes("\r\nDAQIFI>"))
            .ToArray();

        var parsed = ParseInReads(data);

        Assert.Equal(status, Assert.Single(parsed));
    }

    [Theory]
    [InlineData("SYSTem:SYSInfoPB?\r\n")]
    [InlineData("\r\nDAQIFI>SYSTem:SYSInfoPB?\r\n")]
    [InlineData("SYSTem:STReam:INTerface 0\r\nSYSTem:SYSInfoPB?\r\n")]
    public void EchoedLinesAheadOfAStatusReplyFedOneByteAtATime_ComeOutOnce(string echo)
    {
        var status = CompositeMessageParserBenchCaptureTests.BuildStatusMessage();
        var data = Encoding.ASCII.GetBytes(echo).Concat(Delimited(status)).ToArray();

        var parsed = ParseInReads(data.Select(b => new[] { b }).ToArray());

        Assert.Equal(status, Assert.Single(parsed));
    }
}
