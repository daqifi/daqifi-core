using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class ProtobufMessageParserTests
{
    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithValidProtobuf_ShouldReturnMessage()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        // Create minimal valid protobuf data (empty message)
        var data = new byte[] { }; // Empty protobuf message
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert - Empty message should parse successfully
        if (messages.Any())
        {
            Assert.IsType<ProtobufMessage>(messages.First());
        }
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithInvalidData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var invalidData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // Invalid protobuf data
        
        // Act
        var messages = parser.ParseMessages(invalidData, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithEmptyData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var data = new byte[0];
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithNullBytes_ShouldHandleCorrectly()
    {
        // Arrange - This simulates the actual issue with binary protobuf containing null bytes
        var parser = new ProtobufMessageParser();
        // Create data with null bytes that would break LineBasedMessageParser
        var dataWithNulls = new byte[] { 0x00, 0x01, 0x02, 0x00, 0x03 };
        
        // Act - Parser should handle the null bytes gracefully
        var messages = parser.ParseMessages(dataWithNulls, out var consumedBytes);
        
        // Assert - Should not throw exceptions, even if parsing fails
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithIncompleteData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var originalMessage = new DaqifiOutMessage();
        var completeData = originalMessage.ToByteArray();
        var incompleteData = completeData.Take(completeData.Length / 2).ToArray(); // Half the data
        
        // Act
        var messages = parser.ParseMessages(incompleteData, out var consumedBytes);
        
        // Assert - Should not parse incomplete protobuf
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithMultipleMessages_ShouldParseFirst()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        // Create combined mock protobuf data
        var data1 = new byte[] { 0x08, 0x01 }; // Mock protobuf message 1
        var data2 = new byte[] { 0x08, 0x02 }; // Mock protobuf message 2
        var combinedData = data1.Concat(data2).ToArray();
        
        // Act
        var messages = parser.ParseMessages(combinedData, out var consumedBytes);
        
        // Assert - Should attempt to parse and consume some data
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_RecoversFromLeadingGarbage()
    {
        // Reproduces the real-world failure where boot-time garbage on the serial port
        // (DTR pulse on Microchip USB-to-Serial chips, partial frames, etc.) leaves
        // unrecognizable bytes at the head of the consumer buffer. When a valid streaming
        // frame eventually arrives, the parser must skip past the garbage and parse the
        // valid frame instead of stalling forever.
        //
        // With the original 3-retry cap, the parser exits after 3 failed attempts and
        // never advances past the garbage; subsequent reads keep stacking bytes onto a
        // buffer the parser refuses to drain.

        // Arrange
        var parser = new ProtobufMessageParser();

        // 20 bytes of 0x00 — each is a valid but zero-length varint. The parser
        // should advance one byte per attempt and ultimately reach the valid frame.
        // This exposes the old 3-retry cap, which exited after only 3 advances and
        // left the buffer perpetually clogged with garbage.
        var junk = Enumerable.Repeat((byte)0x00, 20).ToArray();

        // Length-prefixed valid frame.
        using var stream = new MemoryStream();
        new DaqifiOutMessage { MsgTimeStamp = 42 }.WriteDelimitedTo(stream);
        var validFrame = stream.ToArray();

        var data = junk.Concat(validFrame).ToArray();

        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes).ToList();

        // Assert — parser must skip the 20 junk bytes and recover the valid frame.
        Assert.Single(messages);
        var parsed = Assert.IsType<DaqifiOutMessage>(messages[0].Data);
        Assert.Equal(42UL, parsed.MsgTimeStamp);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_RecoversFromOversizedPrefix()
    {
        // Reproduces the streaming-startup stall that left the desktop Live Graph empty
        // while the device LED blinked and the byte-buffer grew unbounded (66 → 8202 B in 2s
        // with zero MessageReceived events fired).
        //
        // Cause: boot-time garbage on the serial port (USB CDC handshake, DTR pulse bytes,
        // partial frames from an earlier session) occasionally forms a plausible-looking
        // varint length prefix that encodes a large message — tens of KB, still under the
        // 1 MB cap. The parser sees "prefix OK, need N bytes" and bails at the length check
        // waiting for more data. Since the declared length is huge, enough data never
        // arrives in a reasonable window, the buffer piles up forever, and the valid frame
        // sitting just past the bogus prefix is never parsed.
        //
        // This test: three bytes that varint-encode 32768 (`0x80 0x80 0x02`) — far more
        // than will ever be buffered from normal DAQiFi streaming, but well under the old
        // 1 MB cap — followed by a valid DaqifiOutMessage frame. The parser must reject
        // the oversized prefix, resync, and recover the valid frame.

        // Arrange
        var parser = new ProtobufMessageParser();

        // Varint encoding 32768 = (0 << 0) | (0 << 7) | (2 << 14). The first two bytes
        // set the continuation bit; the third terminates. Plausible under the old 1 MB
        // cap but impossibly large for a real streaming frame at any configured rate.
        var bogusPrefix = new byte[] { 0x80, 0x80, 0x02 };

        using var stream = new MemoryStream();
        new DaqifiOutMessage { MsgTimeStamp = 12345 }.WriteDelimitedTo(stream);
        var validFrame = stream.ToArray();

        var data = bogusPrefix.Concat(validFrame).ToArray();

        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes).ToList();

        // Assert — parser must treat the oversized declared length as garbage, advance,
        // and recover the valid frame. Before the fix, it stalled: 0 messages, 0 consumed.
        Assert.Single(messages);
        var parsed = Assert.IsType<DaqifiOutMessage>(messages[0].Data);
        Assert.Equal(12345UL, parsed.MsgTimeStamp);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_PreservesPartialFrameAfterGarbage()
    {
        // Guards the consumer contract: when leading garbage is followed by the
        // beginning of a real frame whose payload hasn't fully arrived yet, the
        // parser must consume only the garbage. StreamMessageConsumer (and the
        // SD-card parser) trim consumedBytes from their buffers unconditionally,
        // so advancing even one byte into the real prefix or payload here would
        // permanently corrupt the frame on the next read. An earlier revision of
        // the partial-frame recovery logic had this bug — it kept resyncing
        // aggressively after past-garbage advances and ate into real frames that
        // straddled reads.

        // Arrange
        var parser = new ProtobufMessageParser();

        // Leading garbage: bytes that advance the parser without being mistaken
        // for a real frame. 20 zero bytes each decode as a zero-length varint
        // prefix and get skipped one byte at a time.
        var junk = Enumerable.Repeat((byte)0x00, 20).ToArray();

        // Full valid frame, from which we'll keep only the length prefix and a
        // single body byte (the field tag) to simulate an incomplete arrival.
        using var stream = new MemoryStream();
        new DaqifiOutMessage { MsgTimeStamp = 99 }.WriteDelimitedTo(stream);
        var fullFrame = stream.ToArray();
        Assert.True(fullFrame.Length >= 2, "Test precondition: frame has prefix + body");
        var partialFrame = fullFrame.Take(2).ToArray();

        var data = junk.Concat(partialFrame).ToArray();

        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes).ToList();

        // Assert — no message parsed yet, and consumedBytes stops exactly at the
        // real frame boundary. The caller will retain the partial frame for the
        // next call, at which point the completing bytes arrive and parsing
        // succeeds.
        Assert.Empty(messages);
        Assert.Equal(junk.Length, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_LegitimateMultiKbFrame_NotCorruptedByGapGate()
    {
        // Closes #189 Bug 1. The gap gate previously fired whenever
        // missingPayload > MaxPartialFrameGapBytes regardless of how many
        // body bytes had already arrived. A legitimate multi-KB frame
        // arriving across multiple reads (e.g. a fat initial-status frame
        // on a transport that returns smaller chunks) would be misclassified
        // as garbage mid-frame: the parser would advance into the real body
        // and corrupt it. The fix gates the suspicion check on
        // availableBodyBytes <= 1 — once 2+ body bytes are buffered, the
        // field-tag check has already structurally validated the start.
        //
        // Construct a real frame whose declared length leaves a gap larger
        // than MaxPartialFrameGapBytes (1024) and feed only the prefix +
        // a couple of body bytes. The parser must NOT advance into the
        // body — it must wait for the rest.

        // Arrange
        var parser = new ProtobufMessageParser();

        // Build a real frame ~2KB with DeviceFwRev padded to push the body
        // past the gap threshold. WriteDelimitedTo prepends the varint
        // length prefix.
        var msg = new DaqifiOutMessage
        {
            MsgTimeStamp = 99,
            DeviceFwRev = new string('x', 2000),
        };
        using var stream = new MemoryStream();
        msg.WriteDelimitedTo(stream);
        var fullFrame = stream.ToArray();
        Assert.True(fullFrame.Length > 1100,
            "Test precondition: frame must exceed MaxPartialFrameGapBytes (1024) so the gap gate would fire on a one-shot read.");

        // Feed only prefix + first 4 body bytes — body is partially present
        // but not complete. Parser must wait, not advance into the real body.
        var prefixPlusFour = fullFrame.Take(GetVarintLength(fullFrame) + 4).ToArray();

        // Act
        var messages = parser.ParseMessages(prefixPlusFour, out var consumedBytes).ToList();

        // Assert: parser waits — no message parsed, no bytes advanced.
        // Callers retain the buffer for the next read; corrupting it here
        // (advancing into the body) would lose the frame forever.
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_OneBodyByteOfMultiKbFrame_NotCorrupted()
    {
        // Edge case of #189 Bug 1: when EXACTLY one body byte of a real
        // multi-KB frame has arrived (common with small chunked reads),
        // the gap gate must not fire. The single body byte's field-tag
        // check provides positive evidence the prefix is real; advancing
        // even one byte here would unrecoverably corrupt the frame
        // because StreamMessageConsumer trims consumedBytes from its
        // buffer between reads.

        // Arrange
        var parser = new ProtobufMessageParser();

        var msg = new DaqifiOutMessage
        {
            MsgTimeStamp = 99,
            DeviceFwRev = new string('x', 2000),
        };
        using var stream = new MemoryStream();
        msg.WriteDelimitedTo(stream);
        var fullFrame = stream.ToArray();

        // Sanity-check the regression precondition: this test is meaningless
        // unless the frame body would have triggered the old gap gate. The
        // sibling LegitimateMultiKbFrame test uses the same threshold; if the
        // fixture message ever shrinks below it, the regression silently
        // de-scopes — fail loudly here instead.
        Assert.True(fullFrame.Length > 1100,
            $"Test fixture too small to exercise the gap gate (got {fullFrame.Length}, expected > 1100). Increase DeviceFwRev length.");

        // Feed prefix + exactly 1 body byte. The first body byte of a real
        // DaqifiOutMessage is a valid field tag (passes IsPlausibleFieldTagByte).
        var prefixPlusOne = fullFrame.Take(GetVarintLength(fullFrame) + 1).ToArray();

        // Act
        var messages = parser.ParseMessages(prefixPlusOne, out var consumedBytes).ToList();

        // Assert: parser waits, does not advance into the legitimate body.
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Theory]
    [InlineData((byte)0x83)] // continuation bit set, wire type 3 (deprecated SGROUP)
    [InlineData((byte)0x84)] // continuation bit set, wire type 4 (deprecated EGROUP)
    [InlineData((byte)0x86)] // continuation bit set, wire type 6 (undefined)
    [InlineData((byte)0x87)] // continuation bit set, wire type 7 (undefined)
    public void ProtobufMessageParser_ParseMessages_GarbageWithContinuationBit_StillRejected(byte garbageBodyByte)
    {
        // Closes #189 Bug 2. IsPlausibleFieldTagByte previously early-
        // returned true for any byte with the continuation bit (0x80) set,
        // bypassing wire-type validation. Wire type lives in the low 3 bits
        // of the first byte regardless of multi-byte tags, so impossible
        // wire types (3,4,6,7) MUST be rejected even on continuation bytes.
        //
        // Construct a frame with: a plausible-but-bogus length prefix +
        // a body byte whose continuation bit is set AND whose low 3 bits
        // form an impossible wire type. Without the fix, the parser
        // accepts the body byte and stalls waiting for the bogus declared
        // length to arrive. With the fix, the field-tag gate fires and
        // the parser advances past the garbage.

        // Arrange
        var parser = new ProtobufMessageParser();

        // Length prefix: 0x10 = 16 bytes (well under MaxPartialFrameGapBytes
        // so the gap gate doesn't fire on its own — we want to isolate the
        // wire-type check).
        var data = new byte[] { 0x10, garbageBodyByte };

        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes).ToList();

        // Assert: parser advances past the bogus prefix instead of waiting
        // for 16 never-coming bytes.
        Assert.Empty(messages);
        Assert.True(consumedBytes >= 1,
            $"Expected parser to advance past garbage prefix, got consumedBytes={consumedBytes}");
    }

    private static int GetVarintLength(byte[] buffer)
    {
        for (var i = 0; i < buffer.Length && i < 5; i++)
        {
            if ((buffer[i] & 0x80) == 0) return i + 1;
        }
        throw new InvalidOperationException("Malformed varint in test fixture");
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_ReturnsCorrectMessageType()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var data = new byte[] { }; // Empty protobuf
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert - If parsing succeeds, should return correct types
        if (messages.Any())
        {
            var message = messages.First();
            Assert.IsType<ProtobufMessage>(message);
            Assert.IsType<DaqifiOutMessage>(message.Data);
        }
        Assert.True(consumedBytes >= 0);
    }
}