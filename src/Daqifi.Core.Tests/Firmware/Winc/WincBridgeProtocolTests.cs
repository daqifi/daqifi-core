using Daqifi.Core.Firmware.Winc;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// Framing tests for the WINC serial bridge wire format.
/// </summary>
/// <remarks>
/// These are the primary correctness net for the native flasher: the erase/program path cannot be
/// exercised on the bench, so the framing has to be right by construction. Expected byte sequences
/// are derived from the DAQiFi firmware's bridge parser
/// (<c>firmware/src/services/wifi_services/wifi_serial_bridge.c</c>), which is the code that
/// actually accepts or rejects these headers.
/// </remarks>
public class WincBridgeProtocolTests
{
    [Fact]
    public void BuildHeader_ProducesTwelveBytesWhoseXorIsZero()
    {
        // The firmware XORs all 12 bytes and requires 0 before it will ACK.
        var header = WincBridgeProtocol.BuildHeader(
            WincBridgeProtocol.Command.ReadBlock, 0x0100, 0xD0000, 0);

        Assert.Equal(WincBridgeProtocol.HeaderSize, header.Length);

        byte xor = 0;
        foreach (var b in header)
        {
            xor ^= b;
        }

        Assert.Equal(0, xor);
        Assert.True(WincBridgeProtocol.IsHeaderValid(header));
    }

    [Theory]
    [InlineData((byte)0, (ushort)0, 0x1000u, 0u)]           // ReadRegisterWithReturn
    [InlineData((byte)1, (ushort)0, 0x10208u, 0x2Au)]       // WriteRegister
    [InlineData((byte)2, (ushort)2047, 0xD0000u, 0u)]       // ReadBlock, max safe size
    [InlineData((byte)3, (ushort)256, 0xD0000u, 0u)]        // WriteBlock, one flash page
    [InlineData((byte)5, (ushort)0, 0u, 500000u)]           // Reconfigure to the fast baud
    [InlineData((byte)1, (ushort)0xFFFF, 0xFFFFFFFFu, 0xFFFFFFFFu)] // all-ones saturation
    public void BuildHeader_IsAlwaysAcceptedByTheFirmwareChecksumRule(
        byte command, ushort size, uint address, uint value)
    {
        var header = WincBridgeProtocol.BuildHeader(
            (WincBridgeProtocol.Command)command, size, address, value);

        Assert.True(WincBridgeProtocol.IsHeaderValid(header));
    }

    [Fact]
    public void BuildHeader_LaysOutCommandSizeAddressAndValueLittleEndian()
    {
        // Byte-for-byte against the firmware's ProcessHeader field extraction:
        //   size = [3]<<8 | [2];  addr = [7]<<24 | [6]<<16 | [5]<<8 | [4];  val likewise from [8..11].
        var header = WincBridgeProtocol.BuildHeader(
            WincBridgeProtocol.Command.WriteRegister,
            size: 0x1234,
            address: 0xAABBCCDD,
            value: 0x11223344);

        Assert.Equal((byte)WincBridgeProtocol.Command.WriteRegister, header[0]);

        Assert.Equal(0x34, header[2]);
        Assert.Equal(0x12, header[3]);

        Assert.Equal(0xDD, header[4]);
        Assert.Equal(0xCC, header[5]);
        Assert.Equal(0xBB, header[6]);
        Assert.Equal(0xAA, header[7]);

        Assert.Equal(0x44, header[8]);
        Assert.Equal(0x33, header[9]);
        Assert.Equal(0x22, header[10]);
        Assert.Equal(0x11, header[11]);
    }

    [Fact]
    public void BuildHeader_ReconfigurePutsTheBaudRateInTheValueField()
    {
        // The baud change is the one command whose payload is the value field alone.
        var header = WincBridgeProtocol.BuildHeader(
            WincBridgeProtocol.Command.Reconfigure, 0, 0, 500000);

        Assert.Equal(0x05, header[0]);
        Assert.Equal(0x20, header[8]);
        Assert.Equal(0xA1, header[9]);
        Assert.Equal(0x07, header[10]);
        Assert.Equal(0x00, header[11]);
        Assert.True(WincBridgeProtocol.IsHeaderValid(header));
    }

    [Fact]
    public void IsHeaderValid_RejectsASingleFlippedBit()
    {
        var header = WincBridgeProtocol.BuildHeader(
            WincBridgeProtocol.Command.ReadBlock, 512, 0xD0000, 0);

        header[6] ^= 0x01;

        Assert.False(WincBridgeProtocol.IsHeaderValid(header));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    public void IsHeaderValid_RejectsWrongLength(int length)
    {
        Assert.False(WincBridgeProtocol.IsHeaderValid(new byte[length]));
    }

    [Fact]
    public void IsHeaderValid_RejectsNull()
    {
        Assert.False(WincBridgeProtocol.IsHeaderValid(null!));
    }

    [Fact]
    public void ComputeChecksum_IgnoresTheExistingChecksumSlot()
    {
        // Byte 1 must not feed its own computation, or rebuilding a header would drift.
        var header = WincBridgeProtocol.BuildHeader(
            WincBridgeProtocol.Command.ReadBlock, 128, 0x1000, 0);

        var recomputed = WincBridgeProtocol.ComputeChecksum(header);

        Assert.Equal(header[1], recomputed);
    }

    [Fact]
    public void ComputeChecksum_RejectsWrongLengthHeaders()
    {
        Assert.Throws<ArgumentException>(() => WincBridgeProtocol.ComputeChecksum(new byte[5]));
    }

    [Fact]
    public void DecodeRegisterValue_ReadsBigEndian_OppositeOfTheHeaderFields()
    {
        // The firmware writes the register value MSB-first, unlike the little-endian header —
        // getting this backwards is the single easiest way to misread every register.
        var value = WincBridgeProtocol.DecodeRegisterValue([0x00, 0x10, 0x03, 0xA0]);

        Assert.Equal(0x001003A0u, value);
    }

    [Fact]
    public void DecodeRegisterValue_HandlesTheFullRange()
    {
        Assert.Equal(0xFFFFFFFFu, WincBridgeProtocol.DecodeRegisterValue([0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal(0u, WincBridgeProtocol.DecodeRegisterValue([0x00, 0x00, 0x00, 0x00]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void DecodeRegisterValue_RejectsWrongLength(int length)
    {
        Assert.Throws<ArgumentException>(() => WincBridgeProtocol.DecodeRegisterValue(new byte[length]));
    }

    [Fact]
    public void OpCodesAndResponses_MatchTheFirmwareConstants()
    {
        // Pinned against wifi_serial_bridge.c; a silent drift here would break every exchange.
        Assert.Equal(0x12, WincBridgeProtocol.IdentifyVariableBaud);
        Assert.Equal(0x13, WincBridgeProtocol.IdentifyFixedBaud);
        Assert.Equal(0xA5, WincBridgeProtocol.StartCommand);

        Assert.Equal(0x5A, WincBridgeProtocol.Response.Nack);
        Assert.Equal(0x5B, WincBridgeProtocol.Response.IdVariableBaud);
        Assert.Equal(0x5C, WincBridgeProtocol.Response.IdFixedBaud);
        Assert.Equal(0xAC, WincBridgeProtocol.Response.Ack);

        Assert.Equal(0, (byte)WincBridgeProtocol.Command.ReadRegisterWithReturn);
        Assert.Equal(1, (byte)WincBridgeProtocol.Command.WriteRegister);
        Assert.Equal(2, (byte)WincBridgeProtocol.Command.ReadBlock);
        Assert.Equal(3, (byte)WincBridgeProtocol.Command.WriteBlock);
        Assert.Equal(5, (byte)WincBridgeProtocol.Command.Reconfigure);
    }

    [Fact]
    public void MaxReadBlockSize_StaysBelowTheFirmwareCommandBuffer()
    {
        // The device's read loop never terminates at or above its 2048-byte buffer, so 2047 is a
        // hard ceiling rather than a tuning choice.
        Assert.Equal(2047, WincBridgeProtocol.MaxReadBlockSize);
    }
}
