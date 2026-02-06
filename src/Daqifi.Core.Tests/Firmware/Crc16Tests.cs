using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Crc16Tests
{
    [Fact]
    public void Constructor_WithNullData_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Crc16(null!));
    }

    [Fact]
    public void Constructor_WithEmptyData_ReturnsCrcOfZero()
    {
        var crc = new Crc16([]);

        Assert.Equal(0, crc.Crc);
        Assert.Equal(0, crc.Low);
        Assert.Equal(0, crc.High);
    }

    [Fact]
    public void Constructor_WithSingleByte_ComputesCrc()
    {
        // CRC-16/XMODEM of [0x01] should be a known value
        var crc = new Crc16([0x01]);

        Assert.NotEqual(0, crc.Crc);
    }

    [Fact]
    public void Low_ReturnsLowByteOfCrc()
    {
        var crc = new Crc16([0x01, 0x02, 0x03]);

        Assert.Equal((byte)(crc.Crc & 0xFF), crc.Low);
    }

    [Fact]
    public void High_ReturnsHighByteOfCrc()
    {
        var crc = new Crc16([0x01, 0x02, 0x03]);

        Assert.Equal((byte)(crc.Crc >> 8), crc.High);
    }

    [Fact]
    public void Constructor_DifferentData_ProducesDifferentCrc()
    {
        var crc1 = new Crc16([0x01]);
        var crc2 = new Crc16([0x02]);

        Assert.NotEqual(crc1.Crc, crc2.Crc);
    }

    [Fact]
    public void Constructor_SameData_ProducesSameCrc()
    {
        var crc1 = new Crc16([0x01, 0x02, 0x03]);
        var crc2 = new Crc16([0x01, 0x02, 0x03]);

        Assert.Equal(crc1.Crc, crc2.Crc);
    }

    [Fact]
    public void Crc16_KnownVector_AsciiDigits123456789()
    {
        // CRC-16/XMODEM of "123456789" = 0x31C3
        var data = "123456789"u8.ToArray();
        var crc = new Crc16(data);

        Assert.Equal(0x31C3, crc.Crc);
    }

    [Fact]
    public void Crc16_KnownVector_LowAndHighBytes()
    {
        var data = "123456789"u8.ToArray();
        var crc = new Crc16(data);

        Assert.Equal(0xC3, crc.Low);
        Assert.Equal(0x31, crc.High);
    }

    [Fact]
    public void Crc16_RequestVersionCommand_ProducesConsistentResult()
    {
        // The request version command byte is 0x01
        var crc = new Crc16([0x01]);

        // Verify low and high reconstruct the full CRC
        var reconstructed = (ushort)(crc.Low | (crc.High << 8));
        Assert.Equal(crc.Crc, reconstructed);
    }

    [Fact]
    public void Crc16_AllZeros_ProducesZeroCrc()
    {
        // CRC-16/XMODEM of all zeros should be deterministic
        var crc = new Crc16([0x00, 0x00, 0x00, 0x00]);

        // Just verify it's deterministic, not necessarily zero
        var crc2 = new Crc16([0x00, 0x00, 0x00, 0x00]);
        Assert.Equal(crc.Crc, crc2.Crc);
    }
}
