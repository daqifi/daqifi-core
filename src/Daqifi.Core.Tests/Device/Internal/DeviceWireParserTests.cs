using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;
using Google.Protobuf;
using System.Text;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Coverage for the parser behind the device's one reader (issue #900): it must frame protobuf
/// exactly as <see cref="ProtobufMessageParser"/> does, frame reply lines exactly as the separate
/// text reader did, and tag every line with the exchange it was framed for.
/// </summary>
public class DeviceWireParserTests
{
    private static DaqifiOutMessage Status(string pn = "Nq1") => new()
    {
        DevicePn = pn,
        DeviceFwRev = "3.8.0",
        AnalogInPortNum = 16,
        DigitalPortNum = 16,
    };

    private static byte[] Delimited(params DaqifiOutMessage[] messages)
    {
        using var buffer = new MemoryStream();
        foreach (var message in messages)
        {
            message.WriteDelimitedTo(buffer);
        }

        return buffer.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void WithoutACapture_FramesProtobuf()
    {
        var parser = new DeviceWireParser();
        var data = Delimited(Status("A"), Status("B"));

        var messages = parser.ParseMessages(data, out var consumed).Select(m => m.Data).ToList();

        Assert.Equal(data.Length, consumed);
        Assert.Equal(new[] { "A", "B" }, messages.Select(m => m.Frame!.DevicePn));
        Assert.All(messages, m => Assert.Null(m.Line));
        Assert.All(messages, m => Assert.Null(m.Capture));
    }

    [Theory]
    [InlineData("whole frames")]
    [InlineData("partial trailing frame")]
    [InlineData("leading echo noise")]
    [InlineData("garbage between frames")]
    public void WithoutACapture_FramesExactlyAsTheProtobufParserDoes(string shape)
    {
        // The reader's protobuf framing is ProtobufMessageParser's, step for step: the same
        // consumed count and the same frames on every shape the stream parser's own tests pin,
        // including the #268 echo prefix and a frame split across reads.
        var frames = Delimited(Status("A"), Status("B"));
        var data = shape switch
        {
            "whole frames" => frames,
            "partial trailing frame" => frames[..^5],
            "leading echo noise" => Concat(Ascii("SYSTem:SYSInfoPB?\r\n"), Delimited(Status("A")), Ascii("\r\nDAQIFI>")),
            "garbage between frames" => Concat(Delimited(Status("A")), new byte[] { 0xFF, 0xFF, 0xFF }, Delimited(Status("B"))),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var expected = new ProtobufMessageParser().ParseMessages(data, out var expectedConsumed).ToList();
        var actual = new DeviceWireParser().ParseMessages(data, out var actualConsumed).ToList();

        Assert.Equal(expectedConsumed, actualConsumed);
        Assert.Equal(
            expected.Select(m => m.Data.ToByteString()),
            actual.Select(m => m.Data.Frame!.ToByteString()));
    }

    [Fact]
    public void WithACapture_FramesLinesTaggedWithThatCapture()
    {
        var capture = new TextReplyCapture(_ => { });
        var parser = new DeviceWireParser { Capture = capture };
        var data = Ascii("DAQiFi,Nq1,7E2815916200E898,01-02\r\n0,\"No error\"\r\n");

        var messages = parser.ParseMessages(data, out var consumed).Select(m => m.Data).ToList();

        Assert.Equal(data.Length, consumed);
        Assert.Equal(new[] { "DAQiFi,Nq1,7E2815916200E898,01-02", "0,\"No error\"" }, messages.Select(m => m.Line));
        Assert.All(messages, m => Assert.Same(capture, m.Capture));
        Assert.All(messages, m => Assert.Null(m.Frame));
    }

    [Fact]
    public void WithACapture_KeepsBareLineFeedRepliesAndBlankLines()
    {
        // The two reply shapes that used to vanish (issue #538): a bare-LF reply, and the lone
        // CRLF an empty SYSTem:LOG? dump consists of. The exchange counts lines to decide that the
        // device answered, so both must come out as lines.
        var parser = new DeviceWireParser { Capture = new TextReplyCapture(_ => { }) };
        var data = Ascii("Log cleared\n\r\n");

        var lines = parser.ParseMessages(data, out var consumed).Select(m => m.Data.Line).ToList();

        Assert.Equal(data.Length, consumed);
        Assert.Equal(new[] { "Log cleared", string.Empty }, lines);
    }

    [Fact]
    public void WithACapture_LeavesAnUnfinishedLineForTheNextRead()
    {
        var parser = new DeviceWireParser { Capture = new TextReplyCapture(_ => { }) };
        var data = Ascii("0,\"No error\"\r\nDAQiFi,Nq1");

        var lines = parser.ParseMessages(data, out var consumed).Select(m => m.Data.Line).ToList();

        Assert.Equal(new[] { "0,\"No error\"" }, lines);
        Assert.Equal("0,\"No error\"\r\n".Length, consumed);
    }

    [Fact]
    public void ThreeSwitches_FrameEachBufferTheWayTheCaptureInForceSays()
    {
        var capture = new TextReplyCapture(_ => { });
        var parser = new DeviceWireParser();

        var before = parser.ParseMessages(Delimited(Status("A")), out _).Single().Data;
        parser.Capture = capture;
        var during = parser.ParseMessages(Ascii("0,\"No error\"\r\n"), out _).Single().Data;
        parser.Capture = null;
        var after = parser.ParseMessages(Delimited(Status("B")), out _).Single().Data;

        Assert.Equal("A", before.Frame!.DevicePn);
        Assert.Equal("0,\"No error\"", during.Line);
        Assert.Same(capture, during.Capture);
        Assert.Equal("B", after.Frame!.DevicePn);
    }

    [Fact]
    public void SpanAndArrayEntryPoints_Agree()
    {
        var parser = new DeviceWireParser();
        var data = Delimited(Status("A"), Status("B"));

        var fromArray = parser.ParseMessages(data, out var arrayConsumed).Select(m => m.Data.Frame!.DevicePn).ToList();
        var fromSpan = parser.ParseMessages(new ReadOnlySpan<byte>(data), out var spanConsumed).Select(m => m.Data.Frame!.DevicePn).ToList();

        Assert.Equal(arrayConsumed, spanConsumed);
        Assert.Equal(fromArray, fromSpan);
    }

    [Fact]
    public void Capture_DeliversUntilClosedAndDropsAfterwards()
    {
        var delivered = new List<string>();
        var capture = new TextReplyCapture(delivered.Add);

        capture.Deliver("first");
        capture.Close();
        capture.Deliver("late");
        capture.Close();

        Assert.True(capture.IsClosed);
        Assert.Equal(new[] { "first" }, delivered);
    }

    [Fact]
    public void WireMessage_IsExactlyOneOfAFrameOrALine()
    {
        var capture = new TextReplyCapture(_ => { });
        var frame = WireMessage.ForFrame(Status());
        var line = WireMessage.ForLine("x", capture);

        Assert.NotNull(frame.Frame);
        Assert.Null(frame.Line);
        Assert.Null(frame.Capture);
        Assert.Same(frame, ((IInboundMessage<WireMessage>)frame).Data);

        Assert.Null(line.Frame);
        Assert.Equal("x", line.Line);
        Assert.Same(capture, line.Capture);

        Assert.Throws<ArgumentNullException>(() => WireMessage.ForFrame(null!));
        Assert.Throws<ArgumentNullException>(() => WireMessage.ForLine(null!, capture));
        Assert.Throws<ArgumentNullException>(() => WireMessage.ForLine("x", null!));
    }
}
