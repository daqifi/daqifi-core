using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Pic32BootloaderMessageConsumerTests
{
    private const byte SOH = 0x01;
    private const byte EOT = 0x04;
    private const byte DLE = 0x10;

    // Opcodes, as the firmware's T_COMMANDS enumerates them.
    private const byte VersionCommand = 0x01;
    private const byte EraseCommand = 0x02;
    private const byte ProgramCommand = 0x03;
    private const byte ReadCrcCommand = 0x04;
    private const byte JumpToApplicationCommand = 0x05;

    #region Response framing

    /// <summary>
    /// Frames a response body exactly as the firmware does in <c>BOOTLOADER_SEND_RESPONSE</c>: a
    /// CRC-16 over the content is appended to it, then the whole body is SOH/EOT-framed with
    /// DLE escaping of any SOH/EOT/DLE byte.
    /// </summary>
    /// <param name="content">The echoed opcode and whatever the reply answers with.</param>
    /// <param name="overrideCrc">A wrong CRC to stamp instead of the correct one.</param>
    private static byte[] FrameResponse(byte[] content, ushort? overrideCrc = null)
    {
        var crc = overrideCrc ?? new Crc16(content).Crc;

        var body = new List<byte>(content)
        {
            (byte)(crc & 0xFF),
            (byte)(crc >> 8)
        };

        return FrameBody([.. body]);
    }

    /// <summary>SOH/EOT-frames a body with DLE escaping, and appends no CRC of its own.</summary>
    private static byte[] FrameBody(byte[] body)
    {
        var framed = new List<byte> { SOH };
        foreach (var b in body)
        {
            if (b is SOH or EOT or DLE)
            {
                framed.Add(DLE);
            }
            framed.Add(b);
        }
        framed.Add(EOT);
        return framed.ToArray();
    }

    #endregion

    #region Malformed frames

    /// <summary>
    /// The ways a bootloader reply can arrive damaged. Each one is applied to every decoder's own
    /// valid frame, so one list of faults covers all four replies.
    /// </summary>
    public enum FrameFault
    {
        /// <summary>Nothing came back at all.</summary>
        Empty,

        /// <summary>The frame stops after two bytes — a short HID read (issue #841).</summary>
        TruncatedToTwoBytes,

        /// <summary>Every byte arrived except the terminating EOT.</summary>
        NoEot,

        /// <summary>A complete frame, but its CRC-16 does not match its content.</summary>
        WrongCrc,

        /// <summary>A complete frame that does not start with SOH.</summary>
        NoSoh,

        /// <summary>A complete, CRC-correct frame that echoes some other command.</summary>
        WrongCommandEcho,

        /// <summary>A complete, CRC-correct frame carrying one content byte too many.</summary>
        OneContentByteTooMany
    }

    /// <summary>
    /// Every fault, with the <see cref="InvalidDataException"/> message
    /// <see cref="Pic32BootloaderMessageConsumer.DecodeReadCrcResponse"/> reports for it. The
    /// message is what a caller reads in a failed flash log, so it is pinned, not just the type.
    /// </summary>
    public static TheoryData<FrameFault, string> FrameFaults() => new()
    {
        { FrameFault.Empty, "READ_CRC response was empty." },
        { FrameFault.TruncatedToTwoBytes, "READ_CRC response was not EOT-terminated." },
        { FrameFault.NoEot, "READ_CRC response was not EOT-terminated." },
        { FrameFault.WrongCrc, "READ_CRC response CRC mismatch: frame=0x" },
        { FrameFault.NoSoh, "READ_CRC response did not begin with SOH framing." },
        { FrameFault.WrongCommandEcho, "READ_CRC response had unexpected command byte 0x05 (expected 0x04)." },
        { FrameFault.OneContentByteTooMany, "READ_CRC response had 4 content byte(s); expected 3." }
    };

    /// <summary>Applies one fault to the valid frame for the given response content.</summary>
    private static byte[] Malformed(FrameFault fault, byte[] content)
    {
        switch (fault)
        {
            case FrameFault.Empty:
                return [];

            case FrameFault.TruncatedToTwoBytes:
                return FrameResponse(content)[..2];

            case FrameFault.NoEot:
                return FrameResponse(content)[..^1];

            case FrameFault.WrongCrc:
                return FrameResponse(content, (ushort)(new Crc16(content).Crc ^ 0xFFFF));

            case FrameFault.NoSoh:
                var unframed = FrameResponse(content);
                unframed[0] = 0x00;
                return unframed;

            case FrameFault.WrongCommandEcho:
                // Correctly framed and CRC'd, but the echo is JMP_TO_APP, which answers nothing.
                return FrameResponse([JumpToApplicationCommand, .. content[1..]]);

            case FrameFault.OneContentByteTooMany:
                return FrameResponse([.. content, (byte)0x00]);

            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unhandled frame fault.");
        }
    }

    /// <summary>
    /// The point of issue #841. Before the frame check, a damaged reply was read as an answer: a
    /// two-byte read reported bootloader version "0.0", and a truncated or CRC-corrupt
    /// erase/program reply counted as an acknowledgment, so programming carried on past a failed
    /// write. Each decoder must now reject every one of these frames through its own channel —
    /// "Error" for the version, false for an ack, and a named exception for READ_CRC.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrameFaults))]
    public void MalformedFrame_IsRejectedByEveryDecoder(FrameFault fault, string expectedReadCrcMessage)
    {
        Assert.Equal(
            "Error",
            Pic32BootloaderMessageConsumer.DecodeVersionResponse(
                Malformed(fault, [VersionCommand, 0x02, 0x05])));

        Assert.False(
            Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(
                Malformed(fault, [EraseCommand])));

        Assert.False(
            Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(
                Malformed(fault, [ProgramCommand])));

        var ex = Assert.Throws<InvalidDataException>(
            () => Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(
                Malformed(fault, [ReadCrcCommand, 0xCD, 0xAB])));

        Assert.StartsWith(expectedReadCrcMessage, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A frame that is well formed but carries no CRC at all. The acks are the interesting case:
    /// their content is one byte, so a CRC-less ack frame is three bytes shorter than any valid
    /// reply and cannot be CRC-checked at all.
    /// </summary>
    [Fact]
    public void FrameWithoutItsCrc_IsRejectedByEveryDecoder()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(FrameBody([EraseCommand])));
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(FrameBody([ProgramCommand])));

        var ex = Assert.Throws<InvalidDataException>(
            () => Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(FrameBody([ReadCrcCommand])));

        Assert.Equal(
            "READ_CRC response was too short (1 byte(s) inside the frame; an echoed opcode and "
            + "its 2-byte CRC need at least 3).",
            ex.Message);
    }

    /// <summary>
    /// The exact bytes issue #841 reports: a two-byte read. The version decoder used to answer
    /// "0.0" for these, which a caller could not tell from a real answer.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { SOH, DLE })]
    [InlineData(new byte[] { SOH, VersionCommand })]
    [InlineData(new byte[] { SOH, 0x00 })]
    public void DecodeVersionResponse_TwoByteReply_NeverReportsAVersion(byte[] data)
    {
        Assert.Equal("Error", Pic32BootloaderMessageConsumer.DecodeVersionResponse(data));
    }

    #endregion

    #region DecodeVersionResponse

    [Theory]
    [InlineData((byte)2, (byte)5, "2.5")]
    [InlineData((byte)2, (byte)0, "2.0")] // what the bench device's bootloader reports
    [InlineData((byte)1, (byte)4, "1.4")]
    [InlineData((byte)0, (byte)0, "0.0")] // only ever a version when the CRC says so
    [InlineData((byte)SOH, (byte)3, "1.3")] // major collides with SOH → escaped on the wire
    [InlineData((byte)2, (byte)DLE, "2.16")] // minor collides with DLE → escaped
    [InlineData((byte)EOT, (byte)DLE, "4.16")] // both collide → both escaped
    [InlineData((byte)255, (byte)255, "255.255")]
    public void DecodeVersionResponse_ValidFrame_ReturnsVersionString(byte major, byte minor, string expected)
    {
        var framed = FrameResponse([VersionCommand, major, minor]);

        Assert.Equal(expected, Pic32BootloaderMessageConsumer.DecodeVersionResponse(framed));
    }

    [Fact]
    public void DecodeVersionResponse_WithEmptyData_ReturnsError()
    {
        Assert.Equal("Error", Pic32BootloaderMessageConsumer.DecodeVersionResponse([]));
    }

    [Fact]
    public void DecodeVersionResponse_WithoutSohStart_ReturnsError()
    {
        Assert.Equal(
            "Error",
            Pic32BootloaderMessageConsumer.DecodeVersionResponse([0x00, DLE, VersionCommand, 0x02, 0x03]));
    }

    [Fact]
    public void DecodeVersionResponse_AnotherReplysFrame_ReturnsError()
    {
        // A perfectly good ERASE_FLASH acknowledgment is not a version answer.
        Assert.Equal(
            "Error",
            Pic32BootloaderMessageConsumer.DecodeVersionResponse(FrameResponse([EraseCommand])));
    }

    #endregion

    #region DecodeProgramFlashResponse

    [Fact]
    public void DecodeProgramFlashResponse_ValidResponse_ReturnsTrue()
    {
        Assert.True(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(FrameResponse([ProgramCommand])));
    }

    [Fact]
    public void DecodeProgramFlashResponse_WrongCommand_ReturnsFalse()
    {
        // A valid, CRC-correct erase acknowledgment, which is not a program acknowledgment.
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(FrameResponse([EraseCommand])));
    }

    #endregion

    #region DecodeEraseFlashResponse

    [Fact]
    public void DecodeEraseFlashResponse_ValidResponse_ReturnsTrue()
    {
        Assert.True(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(FrameResponse([EraseCommand])));
    }

    [Fact]
    public void DecodeEraseFlashResponse_WrongCommand_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(FrameResponse([ProgramCommand])));
    }

    #endregion

    #region Shared acknowledgment framing

    // The two ack decoders read one framing rule and differ only in which opcode they accept.
    // Nothing above pins the shared half: each decoder's rejection cases were asserted on their
    // own, so the two rules could have drifted apart without a test noticing. This asserts the
    // frames one decoder rejects are rejected by the other as well.
    [Theory]
    [InlineData(new byte[] { })]              // empty
    [InlineData(new byte[] { SOH })]          // SOH only
    [InlineData(new byte[] { 0x00, 0x02 })]   // erase opcode, but not SOH-framed
    [InlineData(new byte[] { 0x00, 0x03 })]   // program opcode, but not SOH-framed
    [InlineData(new byte[] { SOH, 0xFF })]    // not an opcode either one answers
    [InlineData(new byte[] { SOH, 0x02 })]    // the bare echo issue #841 used to accept
    [InlineData(new byte[] { SOH, 0x03 })]
    public void BadlyFramedAcknowledgment_IsRejectedByBothAckDecoders(byte[] response)
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(response));
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(response));
    }

    #endregion

    #region Null input

    // DecodeReadCrcResponse has always answered a null frame with a named ArgumentNullException,
    // but nothing pinned that. It is the reference the other three decoders are aligned to, so
    // pin it first: the exact exception type (not the InvalidDataException this method throws for
    // every other malformed frame), the parameter it blames, and the message a caller sees.
    [Fact]
    public void DecodeReadCrcResponse_WithNullData_ThrowsArgumentNullExceptionNamingData()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(null!));

        Assert.Equal("data", ex.ParamName);
        Assert.Equal(new ArgumentNullException("data").Message, ex.Message);
    }

    // The point of the change: one catch covers all four decoders, and a caller that learns one
    // decoder's null behavior has learned all of them. Comparing against the exception
    // DecodeReadCrcResponse throws — the one that was always guarded — keeps the three converted
    // decoders pinned to it rather than to a copy of the expected text.
    [Fact]
    public void AllFourDecoders_WithNullData_ThrowTheSameArgumentNullException()
    {
        var reference = Assert.Throws<ArgumentNullException>(
            () => Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(null!));

        Action[] others =
        [
            () => Pic32BootloaderMessageConsumer.DecodeVersionResponse(null!),
            () => Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(null!),
            () => Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(null!)
        ];

        foreach (var decode in others)
        {
            var ex = Assert.Throws<ArgumentNullException>(decode);

            Assert.Equal(reference.ParamName, ex.ParamName);
            Assert.Equal(reference.Message, ex.Message);
        }
    }

    #endregion

    #region DecodeReadCrcResponse

    [Theory]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0xABCD)]
    [InlineData((ushort)0x0104)] // bytes collide with SOH/EOT → exercises escaping
    [InlineData((ushort)0x1004)] // bytes collide with DLE/EOT → exercises escaping
    [InlineData((ushort)0xFFFF)]
    public void DecodeReadCrcResponse_RoundTripsFlashCrc(ushort flashCrc)
    {
        var framed = FrameResponse([ReadCrcCommand, (byte)(flashCrc & 0xFF), (byte)(flashCrc >> 8)]);

        var decoded = Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(framed);

        Assert.Equal(flashCrc, decoded);
    }

    [Fact]
    public void DecodeReadCrcResponse_AnotherReplysFrame_Throws()
    {
        // A valid program acknowledgment carries no flash CRC to report.
        var ex = Assert.Throws<InvalidDataException>(
            () => Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(FrameResponse([ProgramCommand])));

        Assert.Equal(
            "READ_CRC response had unexpected command byte 0x03 (expected 0x04).",
            ex.Message);
    }

    #endregion
}
