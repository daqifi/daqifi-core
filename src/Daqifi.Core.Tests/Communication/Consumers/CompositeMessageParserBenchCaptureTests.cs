using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Routing tests built from fixtures shaped like raw bytes captured off an Nq1 running
/// firmware 3.7.2 over USB CDC (issue #268). Each fixture reproduces the byte statistics
/// the real capture had — printable-ASCII ratio, null-byte ratio, frame sizes and framing
/// — because those statistics are exactly what <see cref="CompositeMessageParser"/>
/// classifies on. The shape assertions below are part of the test: if a fixture drifts
/// away from the measured capture it stops standing in for the hardware and the routing
/// assertion beneath it stops meaning anything.
/// </summary>
public class CompositeMessageParserBenchCaptureTests
{
    private static double PrintableRatio(byte[] data) =>
        data.Count(b => b >= 32 && b <= 126) / (double)data.Length;

    private static double NullRatio(byte[] data) =>
        data.Count(b => b == 0) / (double)data.Length;

    private static byte[] Delimited(DaqifiOutMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteDelimitedTo(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A SYSTem:SYSInfoPB? reply: device identity, network configuration and per-channel
    /// calibration. Sized and populated so the delimited frame matches the capture — a
    /// two-byte "C8 04" varint prefix plus a 584-byte body — and lands on the capture's
    /// byte statistics (24.0% printable, 40.5% null).
    /// </summary>
    private static DaqifiOutMessage BuildStatusMessage()
    {
        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceHwRev = "1.0",
            DeviceFwRev = "3.7.2",
            DeviceSn = 1234567,
            DevicePort = 9760,
            MacAddr = ByteString.CopyFrom(new byte[] { 0x00, 0x1E, 0xC0, 0x11, 0x22, 0x33 }),
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 42 }),
            NetMask = ByteString.CopyFrom(new byte[] { 255, 255, 255, 0 }),
            Gateway = ByteString.CopyFrom(new byte[] { 192, 168, 1, 1 }),
            HostName = "DAQiFi",
            Ssid = "bench-n",
            SsidStrength = 72,
            WifiSecurityMode = 3,
            WifiInfMode = 1,
            TimestampFreq = 50000,
            AnalogInPortNum = 16,
            AnalogInPortNumPriv = 2,
            AnalogInPortType = ByteString.CopyFrom(new byte[] { 0x00, 0x00 }),
            AnalogInPortRse = ByteString.CopyFrom(new byte[] { 0xFF, 0xFF }),
            AnalogInPortEnabled = ByteString.CopyFrom(new byte[] { 0x01, 0x00 }),
            AnalogInRes = 16,
            AnalogInResPriv = 12,
            DigitalPortNum = 16,
            DigitalPortType = ByteString.CopyFrom(new byte[] { 0x00, 0x00 }),
            DigitalPortDir = ByteString.CopyFrom(new byte[] { 0x00, 0x00 }),
            AnalogOutPortNum = 0,
            AnalogOutRes = 12,
            IpAddrV6 = ByteString.CopyFrom(new byte[] { 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0x02, 0x1E, 0xC0, 0xFF, 0xFE, 0x11, 0x22, 0x33 }),
            GatewayV6 = ByteString.CopyFrom(new byte[] { 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0x02, 0x1E, 0xC0, 0xFF, 0xFE, 0x11, 0x22, 0x01 }),
            PrimaryDnsV6 = ByteString.CopyFrom(new byte[] { 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0x02, 0x1E, 0xC0, 0xFF, 0xFE, 0x11, 0x22, 0x02 }),
            SecondaryDnsV6 = ByteString.CopyFrom(new byte[] { 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0x02, 0x1E, 0xC0, 0xFF, 0xFE, 0x11, 0x22, 0x03 }),
            Eui64 = ByteString.CopyFrom(new byte[] { 0x02, 0x1E, 0xC0, 0xFF, 0xFE, 0x11, 0x22, 0x33 }),
            SubPreLengthV6 = ByteString.CopyFrom(new byte[] { 64 }),
            PwrStatus = 3,
            BattStatus = 92,
            TempStatus = 31,
            DeviceStatus = 1,
        };

        for (var channel = 0; channel < 16; channel++)
        {
            status.AnalogInCalM.Add(1.0f);
            status.AnalogInCalB.Add(0.0f);
            status.AnalogInIntScaleM.Add(10.0f);
            status.AnalogInPortAvRange.Add(10.0f);
        }

        // Private-channel calibration. The counts are what bring the encoded body to the
        // captured 584 bytes at the captured printable/null mix.
        for (var i = 0; i < 4; i++)
        {
            status.AnalogInPortAvRangePriv.Add(0.0f);
        }

        for (var i = 0; i < 15; i++)
        {
            status.AnalogInCalMPriv.Add(1.0f);
        }

        return status;
    }

    /// <summary>
    /// The status reply exactly as it comes off the wire: the length-delimited protobuf
    /// frame followed by the ASCII CRLF the device appends to its binary reply.
    /// </summary>
    private static byte[] BuildStatusReplyCapture() =>
        Delimited(BuildStatusMessage()).Concat(Encoding.ASCII.GetBytes("\r\n")).ToArray();

    /// <summary>
    /// A stream of one analog channel at 10 Hz: 10-byte frames whose every byte is either
    /// a varint continuation byte or a small value, so the buffer contains no null bytes
    /// and no printable ASCII whatsoever.
    /// </summary>
    private static byte[] BuildSingleChannelStreamCapture()
    {
        var capture = new List<byte>();
        for (uint sample = 0; sample < 15; sample++)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = 100_000u + sample * 5_000u };
            frame.AnalogInData.Add(20_000 + (int)sample * 37);
            capture.AddRange(Delimited(frame));
        }

        return capture.ToArray();
    }

    /// <summary>
    /// A stream of four analog channels at 100 Hz over ~3 s: 11-byte frames in which
    /// channels resting near zero encode as null bytes, giving the capture's 16.6% null
    /// and 8.3% printable mix.
    /// </summary>
    private static byte[] BuildFourChannelStreamCapture()
    {
        var capture = new List<byte>();
        for (uint sample = 0; sample < 260; sample++)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = 600_000u + sample * 500u };
            frame.AnalogInData.Add(0);
            frame.AnalogInData.Add(1 + (int)(sample % 3));
            frame.AnalogInData.Add(sample % 6 == 0 ? 2 : 0);
            frame.AnalogInData.Add(-1 - (int)(sample % 2));
            capture.AddRange(Delimited(frame));
        }

        return capture.ToArray();
    }

    [Fact]
    public void StatusReplyCapture_MatchesTheShapeMeasuredOnHardware()
    {
        var capture = BuildStatusReplyCapture();

        // 2-byte varint prefix + 584-byte body + CRLF, as captured.
        Assert.Equal(588, capture.Length);
        Assert.Equal(new byte[] { 0xC8, 0x04 }, capture.Take(2));
        Assert.Equal(new byte[] { 0x0D, 0x0A }, capture.Skip(586));

        // The trailing CRLF sits on a buffer that is overwhelmingly binary: this is the
        // exact combination the old line-ending heuristic read as text.
        Assert.InRange(PrintableRatio(capture), 0.20, 0.28);
        Assert.InRange(NullRatio(capture), 0.36, 0.45);
    }

    [Fact]
    public void StatusReplyCapture_TerminatedWithCrlf_RoutesToProtobuf()
    {
        // Regression for issue #268 finding 2. The device terminates its *binary*
        // SYSTem:SYSInfoPB? reply with an ASCII CRLF. The classifier used to accept any
        // buffer ending in a line ending as text, so LineBasedMessageParser consumed all
        // 588 bytes as a single garbage "line" and the protobuf frame was destroyed.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = BuildStatusReplyCapture();

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        var message = Assert.IsType<DaqifiOutMessage>(Assert.Single(messages).Data);
        Assert.Equal("Nq1", message.DevicePn);
        Assert.Equal(1234567UL, message.DeviceSn);
        Assert.Equal(16u, message.AnalogInPortNum);

        // The frame is consumed; the trailing CRLF is left for the next read.
        Assert.Equal(586, consumedBytes);
    }

    [Fact]
    public void SingleChannelStreamCapture_HasNeitherNullNorPrintableBytes()
    {
        var capture = BuildSingleChannelStreamCapture();

        Assert.Equal(150, capture.Length);
        Assert.Equal(0.0, PrintableRatio(capture));
        Assert.Equal(0.0, NullRatio(capture));
    }

    [Fact]
    public void SingleChannelStreamCapture_WhereEveryRatioHeuristicAbstains_RoutesToProtobuf()
    {
        // One analog channel at 10 Hz produces frames with 0% null bytes and 0% printable
        // ASCII, so the printable-ratio and null-ratio heuristics both abstain and the
        // buffer classifies Uncertain. Uncertain must fall to protobuf first: a buffer
        // that failed the printable test is not plausibly SCPI text, and handing it to
        // the line parser risks losing frames to a stray CRLF-shaped byte pair.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = BuildSingleChannelStreamCapture();

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        Assert.Equal(15, messages.Count);
        Assert.All(messages, m => Assert.IsType<DaqifiOutMessage>(m.Data));
        Assert.Equal(capture.Length, consumedBytes);

        var timestamps = messages.Select(m => ((DaqifiOutMessage)m.Data).MsgTimeStamp).ToList();
        Assert.Equal(100_000u, timestamps[0]);
        Assert.Equal(timestamps.OrderBy(t => t), timestamps);
    }

    [Fact]
    public void FourChannelStreamCapture_MatchesTheShapeMeasuredOnHardware()
    {
        var capture = BuildFourChannelStreamCapture();

        Assert.Equal(11 * 260, capture.Length);
        Assert.InRange(PrintableRatio(capture), 0.06, 0.11);
        Assert.InRange(NullRatio(capture), 0.14, 0.19);
    }

    [Fact]
    public void FourChannelStreamCapture_RoutesToProtobuf()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var capture = BuildFourChannelStreamCapture();

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        Assert.Equal(260, messages.Count);
        Assert.All(messages, m => Assert.IsType<DaqifiOutMessage>(m.Data));
        Assert.Equal(capture.Length, consumedBytes);
        Assert.All(messages, m => Assert.Equal(4, ((DaqifiOutMessage)m.Data).AnalogInData.Count));
    }

    [Fact]
    public void MultiLineScpiReply_RoutesToText()
    {
        // The SYSTem:INFO? style reply captured alongside the binary frames. Several
        // CRLF-terminated key=value lines, each of which must arrive as its own string.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = Encoding.ASCII.GetBytes(
            "HeapTotal=75000\r\nHeapFree=7544\r\nStackTotal=8192\r\nStackFree=6120\r\n");

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.IsType<string>(m.Data));
        Assert.Equal(
            new[] { "HeapTotal=75000", "HeapFree=7544", "StackTotal=8192", "StackFree=6120" },
            messages.Select(m => (string)m.Data));
        Assert.Equal(capture.Length, consumedBytes);
    }

    [Fact]
    public void ScpiErrorReply_RoutesToText()
    {
        // The negative control from the bench pass: SYSTem:NOTAREALCOMMAND? answered with
        // an error line. The leading '*' is also the SCPI marker the classifier looks for.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = Encoding.ASCII.GetBytes("**ERROR: -113, \"Undefined header\"\r\n");

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        var message = Assert.IsType<string>(Assert.Single(messages).Data);
        Assert.Equal("**ERROR: -113, \"Undefined header\"", message);
        Assert.Equal(capture.Length, consumedBytes);
    }

    [Theory]
    [InlineData("0\r\n", "0")]                       // SYSTem:STReam:FORmat?
    [InlineData("1\r\n", "1")]                       // SYSTem:STReam:INTerface?
    [InlineData("7544\r\n", "7544")]                 // HeapFree
    [InlineData("16\r\n", "16")]
    [InlineData("0,\"No error\"\r\n", "0,\"No error\"")] // drained error queue
    public void ShortScpiReply_RoutesToText(string reply, string expected)
    {
        // A short reply is mostly line ending by raw byte count — "0\r\n" is 33% printable
        // — so scoring the line ending against printability drops these below the text
        // threshold and hands them to the protobuf parser first, where a chance parse
        // would consume the reply and destroy it. Line endings are framing, not content:
        // by content these replies are 100% printable and must classify as text outright.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = Encoding.ASCII.GetBytes(reply);

        // Act
        var messages = parser.ParseMessages(capture, out var consumedBytes).ToList();

        // Assert
        Assert.Equal(expected, Assert.IsType<string>(Assert.Single(messages).Data));
        Assert.Equal(capture.Length, consumedBytes);
    }

    [Fact]
    public void EchoedCommandAheadOfStatusFrame_RoutesToProtobuf()
    {
        // With echo on, the device puts the ASCII command in front of its binary reply and
        // a "DAQIFI>" prompt behind it. The buffer then both starts with "SYST" and ends
        // with a text-shaped tail while still being ~40% null bytes — the leading SCPI
        // marker must not outrank that null density.

        // Arrange
        var parser = new CompositeMessageParser();
        var capture = Encoding.ASCII.GetBytes("SYSTem:SYSInfoPB?\r\n")
            .Concat(Delimited(BuildStatusMessage()))
            .Concat(Encoding.ASCII.GetBytes("\r\nDAQIFI>"))
            .ToArray();

        // Act
        var messages = parser.ParseMessages(capture, out _).ToList();

        // Assert
        var message = Assert.IsType<DaqifiOutMessage>(Assert.Single(messages).Data);
        Assert.Equal("Nq1", message.DevicePn);
    }
}
