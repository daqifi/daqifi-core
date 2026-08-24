using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class CompositeMessageParserTests
{
    [Fact]
    public void CompositeMessageParser_ParseMessages_WithTextData_ShouldUseTextParser()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var textData = Encoding.UTF8.GetBytes("*IDN?\r\nDAQiFi Device v1.0\r\n");

        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes);

        // Assert
        Assert.Equal(2, messages.Count());
        Assert.All(messages, msg => Assert.IsType<string>(msg.Data));
        Assert.Equal("*IDN?", messages.First().Data);
        Assert.Equal("DAQiFi Device v1.0", messages.Last().Data);
        Assert.Equal(textData.Length, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithBinaryData_ShouldUseProtobufParser()
    {
        // Arrange - 50% null bytes, well past the 10% null-ratio threshold, so the
        // classifier must hand this to the protobuf parser FIRST. Both parsers are
        // recorded into one shared log so the assertion below sees the real call
        // ORDER, not just which parsers were touched.
        var log = new RoutingLog();
        var protobufResult = new DaqifiOutMessage { MsgTimeStamp = 1234 };
        var parser = new CompositeMessageParser(
            new RecordingTextParser(log, result: "should not be reached", consumed: 4),
            new RecordingProtobufParser(log, result: protobufResult, consumed: 4));
        var binaryDataWithNulls = new byte[] { 0x00, 0x01, 0x00, 0x02 };

        // Act
        var messages = parser.ParseMessages(binaryDataWithNulls, out var consumedBytes).ToList();

        // Assert - the protobuf parser ran, it ran first, and because it produced a
        // message the text parser was never consulted at all. Routing the other way
        // would put "text" in the log and would surface the text parser's string here.
        Assert.Equal(new[] { "protobuf" }, log.Calls);
        Assert.Equal(binaryDataWithNulls, log.Buffers.Single());
        var message = Assert.Single(messages);
        Assert.Same(protobufResult, Assert.IsType<DaqifiOutMessage>(message.Data));
        Assert.Equal(4, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithOneNullByteInPrintableData_ShouldDetectAsText()
    {
        // Arrange - "Hello\0World": 10 of 11 bytes are printable ASCII (90.9%) and only
        // one is a null (9.1%).
        //
        // This test used to be called "...WithNullBytes_ShouldDetectAsBinary" and asserted
        // nothing, so nobody noticed it was named for the opposite of what the classifier
        // does. Printable ratio is weighed BEFORE null ratio on purpose (heuristic 1 in
        // DetectMessageType): a buffer that is over 80% printable is text even with a
        // stray null in it, and a lone null in eleven bytes is below the 10% null-density
        // threshold anyway. The genuinely binary case — 40% nulls, 24% printable — is
        // covered by WithBinaryData_ShouldUseProtobufParser above.
        var log = new RoutingLog();
        var parser = new CompositeMessageParser(
            new RecordingTextParser(log, result: "Hello\0World", consumed: 11),
            new RecordingProtobufParser(log, result: new DaqifiOutMessage(), consumed: 11));
        var dataWithOneNull = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 };

        // Act
        var messages = parser.ParseMessages(dataWithOneNull, out var consumedBytes).ToList();

        // Assert - text parser first, and it answered, so protobuf was never consulted.
        Assert.Equal(new[] { "text" }, log.Calls);
        var message = Assert.Single(messages);
        Assert.Equal("Hello\0World", Assert.IsType<string>(message.Data));
        Assert.Equal(11, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNoNullBytes_ShouldDetectAsText()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var textOnlyData = Encoding.UTF8.GetBytes("Hello World\r\n");

        // Act
        var messages = parser.ParseMessages(textOnlyData, out var consumedBytes);

        // Assert
        Assert.Single(messages);
        Assert.IsType<string>(messages.First().Data);
        Assert.Equal("Hello World", messages.First().Data);
        Assert.Equal(textOnlyData.Length, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithEmptyData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var emptyData = new byte[0];

        // Act
        var messages = parser.ParseMessages(emptyData, out var consumedBytes);

        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithCustomParsers_ShouldUseProvided()
    {
        // Arrange
        var mockTextParser = new LineBasedMessageParser("\n"); // Custom line ending
        var mockProtobufParser = new ProtobufMessageParser();
        var parser = new CompositeMessageParser(mockTextParser, mockProtobufParser);

        var textData = Encoding.UTF8.GetBytes("Line 1\nLine 2\n");

        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes);

        // Assert
        Assert.Equal(2, messages.Count());
        Assert.All(messages, msg => Assert.IsType<string>(msg.Data));
        Assert.Equal("Line 1", messages.First().Data);
        Assert.Equal("Line 2", messages.Last().Data);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNullTextParser_UsesDefaultLineParser()
    {
        // Arrange - a null text parser means "use the default", not "skip text routing".
        // The old version of this test asserted only consumedBytes >= 0 and carried a
        // comment claiming the call would fall back to the protobuf parser; it does not.
        var log = new RoutingLog();
        var protobufSpy = new RecordingProtobufParser(log, result: new DaqifiOutMessage(), consumed: 13);
        var parser = new CompositeMessageParser(null, protobufSpy);
        var textData = Encoding.UTF8.GetBytes("Hello World\r\n");

        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes).ToList();

        // Assert - the substituted LineBasedMessageParser handled the buffer end to end,
        // so the supplied protobuf parser was never reached.
        var message = Assert.Single(messages);
        Assert.Equal("Hello World", Assert.IsType<string>(message.Data));
        Assert.Equal(textData.Length, consumedBytes);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNullProtobufParser_UsesDefaultProtobufParser()
    {
        // Arrange - a null protobuf parser also means "use the default", and the old
        // version of this test (consumedBytes >= 0 on four junk bytes) could not tell
        // whether the substitution happened. Feed a real length-delimited frame instead:
        // only a working ProtobufMessageParser decodes it, and the supplied text parser
        // is rigged to answer, so if routing ever reached it the answer here would be a
        // string instead of a DaqifiOutMessage.
        var log = new RoutingLog();
        var textSpy = new RecordingTextParser(log, result: "text parser must not win", consumed: 3);
        var parser = new CompositeMessageParser(textSpy, null);

        using var stream = new MemoryStream();
        new DaqifiOutMessage { MsgTimeStamp = 99 }.WriteDelimitedTo(stream);
        var protobufFrame = stream.ToArray();

        // Act
        var messages = parser.ParseMessages(protobufFrame, out var consumedBytes).ToList();

        // Assert
        Assert.Empty(log.Calls);
        var message = Assert.Single(messages);
        Assert.Equal(99u, Assert.IsType<DaqifiOutMessage>(message.Data).MsgTimeStamp);
        Assert.Equal(protobufFrame.Length, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WhenPreferredParserFindsNothing_FallsBackWithFullBuffer()
    {
        // Arrange - 25% null bytes, so the protobuf parser is preferred; it is rigged to
        // find nothing while reporting that it consumed 3 bytes resyncing over the junk.
        var log = new RoutingLog();
        var textSpy = new RecordingTextParser(log, result: "fallback line", consumed: 4);
        var protobufSpy = new RecordingProtobufParser(log, result: null, consumed: 3);
        var parser = new CompositeMessageParser(textSpy, protobufSpy);
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        // Act
        var messages = parser.ParseMessages(binaryData, out var consumedBytes).ToList();

        // Assert - the preferred parser runs first, the other runs second, and the second
        // one is handed the WHOLE buffer. That last point is the invariant CompositeMessageParser
        // documents on TryParse: a parser that consumed bytes without producing a message
        // does not get to hide those bytes from the fallback, and its consumedBytes is not
        // adopted (4, from the text parser that actually answered, not 3).
        Assert.Equal(new[] { "protobuf", "text" }, log.Calls);
        Assert.All(log.Buffers, buffer => Assert.Equal(binaryData, buffer));
        var message = Assert.Single(messages);
        Assert.Equal("fallback line", Assert.IsType<string>(message.Data));
        Assert.Equal(4, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_ReturnsDifferentMessageTypes()
    {
        // Arrange - the point of the composite is that one call site can receive both
        // formats, so the two routings must yield genuinely different Data types. The
        // old version of this test ended in Assert.True(binaryMessages.Count() >= 0),
        // which Enumerable.Count() can never violate.
        var parser = new CompositeMessageParser();
        var textData = Encoding.UTF8.GetBytes("Text Message\r\n");

        using var stream = new MemoryStream();
        new DaqifiOutMessage { MsgTimeStamp = 42 }.WriteDelimitedTo(stream);
        var protobufFrame = stream.ToArray();

        // Act
        var textMessages = parser.ParseMessages(textData, out var textConsumed).ToList();
        var binaryMessages = parser.ParseMessages(protobufFrame, out var binaryConsumed).ToList();

        // Assert
        var textMessage = Assert.Single(textMessages);
        Assert.Equal("Text Message", Assert.IsType<string>(textMessage.Data));
        Assert.Equal(textData.Length, textConsumed);

        var binaryMessage = Assert.Single(binaryMessages);
        var decoded = Assert.IsType<DaqifiOutMessage>(binaryMessage.Data);
        Assert.Equal(42u, decoded.MsgTimeStamp);
        Assert.Equal(protobufFrame.Length, binaryConsumed);
    }

    public static TheoryData<string, byte[], string[], int> MixedScenarios() => new()
    {
        // SCPI command echo: >80% printable, routed to text, one complete line.
        { "SCPI command", Encoding.UTF8.GetBytes("*IDN?\r\n"), new[] { "*IDN?" }, 7 },
        { "SCPI query", Encoding.UTF8.GetBytes("SYST:ERR?\r\n"), new[] { "SYST:ERR?" }, 11 },

        // A truncated protobuf-shaped buffer: the 0x0A length prefix declares 10 body
        // bytes but only 4 are present, and there is no CRLF for the text parser either.
        // Neither parser produces a message, so neither parser's consumedBytes is
        // adopted and the caller keeps every byte for the next read.
        { "truncated protobuf frame", new byte[] { 0x0A, 0x04, 0x74, 0x65, 0x73, 0x74 }, Array.Empty<string>(), 0 },

        // 75% nulls: protobuf first, no valid frame, text fallback finds no line.
        { "binary with nulls", new byte[] { 0x00, 0x00, 0x00, 0x01 }, Array.Empty<string>(), 0 },

        // (The empty buffer is not a row here: WithEmptyData_ShouldReturnEmpty above
        // already pins that contract, and it is the one input whose expected result
        // cannot be driven red by mutating either parser, since CompositeMessageParser
        // returns before routing.)

        // A single non-printable byte must NOT be swallowed as a text line.
        { "lone non-printable byte", new byte[] { 0xFF }, Array.Empty<string>(), 0 },
    };

    /// <remarks>
    /// <paramref name="scenario"/> names the row in the runner output and in the failure
    /// messages below; a raw byte[] renders as "Byte[]", which tells a reviewer nothing
    /// about which case broke.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MixedScenarios))]
    public void CompositeMessageParser_ParseMessages_WithMixedScenarios_ProducesExactMessagesAndConsumedBytes(
        string scenario, byte[] data, string[] expectedText, int expectedConsumed)
    {
        // Arrange
        var parser = new CompositeMessageParser();

        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes).ToList();

        // Assert - the old version of this test only checked that nothing threw and that
        // consumed >= 0, which held no matter where the bytes went. Pinning the exact
        // output and the exact consumedBytes is what makes a misroute visible: a buffer
        // routed to the wrong parser either loses its message or reports the wrong
        // number of bytes consumed, and a caller that trims the wrong count corrupts
        // the stream.
        var actualText = messages.Select(m => Assert.IsType<string>(m.Data)).ToArray();
        Assert.True(
            expectedText.SequenceEqual(actualText),
            $"{scenario}: expected [{string.Join(", ", expectedText)}] but got [{string.Join(", ", actualText)}]");
        Assert.True(
            expectedConsumed == consumedBytes,
            $"{scenario}: expected consumedBytes {expectedConsumed} but got {consumedBytes}");
    }

    /// <summary>
    /// Shared, ordered record of which parser <see cref="CompositeMessageParser"/> invoked
    /// and with what bytes. One log is handed to both recording parsers so a test can assert
    /// the routing ORDER, not merely that a parser was reached.
    /// </summary>
    private sealed class RoutingLog
    {
        public List<string> Calls { get; } = [];
        public List<byte[]> Buffers { get; } = [];

        public void Record(string parserName, byte[] data)
        {
            Calls.Add(parserName);
            Buffers.Add(data.ToArray());
        }
    }

    private sealed class RecordingTextParser(RoutingLog log, string? result = null, int consumed = 0)
        : IMessageParser<string>
    {
        public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
        {
            log.Record("text", data);
            consumedBytes = consumed;
            return result is null
                ? []
                : [new TextInboundMessage(result)];
        }
    }

    private sealed class RecordingProtobufParser(RoutingLog log, DaqifiOutMessage? result = null, int consumed = 0)
        : IMessageParser<DaqifiOutMessage>
    {
        public IEnumerable<IInboundMessage<DaqifiOutMessage>> ParseMessages(byte[] data, out int consumedBytes)
        {
            log.Record("protobuf", data);
            consumedBytes = consumed;
            return result is null
                ? []
                : [new ProtobufMessage(result)];
        }
    }
}
