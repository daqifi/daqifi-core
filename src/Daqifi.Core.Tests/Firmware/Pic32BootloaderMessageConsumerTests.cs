using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Pic32BootloaderMessageConsumerTests
{
    private const byte SOH = 0x01;
    private const byte DLE = 0x10;

    #region DecodeVersionResponse

    [Fact]
    public void DecodeVersionResponse_WithTooShortData_ReturnsError()
    {
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse([0x01]);

        Assert.Equal("Error", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithEmptyData_ReturnsError()
    {
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse([]);

        Assert.Equal("Error", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithoutSohStart_ReturnsError()
    {
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse([0x00, 0x10, 0x01, 0x02, 0x03]);

        Assert.Equal("Error", result);
    }

    [Fact]
    public void DecodeVersionResponse_ValidResponse_ReturnsVersionString()
    {
        // SOH + DLE + VersionCmd(0x01) + Major(2) + Minor(5)
        var data = new byte[] { SOH, DLE, 0x01, 0x02, 0x05 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("2.5", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithDleEscapedMajorVersion_ReturnsCorrectVersion()
    {
        // SOH + DLE + VersionCmd(0x01) + DLE + Major(0x01) + Minor(0x03)
        // Major version 0x01 matches SOH so it's DLE-escaped
        var data = new byte[] { SOH, DLE, 0x01, DLE, 0x01, 0x03 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("1.3", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithDleEscapedMinorVersion_ReturnsCorrectVersion()
    {
        // SOH + DLE + VersionCmd(0x01) + Major(0x02) + DLE + Minor(0x10)
        // Minor version 0x10 matches DLE so it's DLE-escaped
        var data = new byte[] { SOH, DLE, 0x01, 0x02, DLE, 0x10 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("2.16", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithBothVersionsEscaped_ReturnsCorrectVersion()
    {
        // SOH + DLE + VersionCmd(0x01) + DLE + Major(0x04) + DLE + Minor(0x10)
        var data = new byte[] { SOH, DLE, 0x01, DLE, 0x04, DLE, 0x10 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("4.16", result);
    }

    [Fact]
    public void DecodeVersionResponse_WithoutDleEscapedCommand_ReturnsZeroVersion()
    {
        // If the command byte isn't DLE-escaped, the parser returns 0.0
        var data = new byte[] { SOH, 0x02, 0x03, 0x04, 0x05 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("0.0", result);
    }

    [Fact]
    public void DecodeVersionResponse_TooShortForVersionBytes_ReturnsZeroVersion()
    {
        // SOH + DLE + cmd but no version bytes (only 3 bytes, need at least 5)
        var data = new byte[] { SOH, DLE, 0x01 };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("0.0", result);
    }

    [Fact]
    public void DecodeVersionResponse_ExactlyTwoBytes_ReturnsZeroVersion()
    {
        // Only 2 bytes: passes length check but can't have DLE+cmd+versions
        var data = new byte[] { SOH, DLE };
        var result = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);

        Assert.Equal("0.0", result);
    }

    #endregion

    #region DecodeProgramFlashResponse

    [Fact]
    public void DecodeProgramFlashResponse_WithTooShortData_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse([0x01]));
    }

    [Fact]
    public void DecodeProgramFlashResponse_WithEmptyData_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse([]));
    }

    [Fact]
    public void DecodeProgramFlashResponse_WithoutSohStart_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse([0x00, 0x03]));
    }

    [Fact]
    public void DecodeProgramFlashResponse_ValidResponse_ReturnsTrue()
    {
        // SOH + ProgramFlashCommand(0x03)
        Assert.True(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse([SOH, 0x03]));
    }

    [Fact]
    public void DecodeProgramFlashResponse_WrongCommand_ReturnsFalse()
    {
        // SOH + EraseCommand(0x02)
        Assert.False(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse([SOH, 0x02]));
    }

    #endregion

    #region DecodeEraseFlashResponse

    [Fact]
    public void DecodeEraseFlashResponse_WithTooShortData_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse([0x01]));
    }

    [Fact]
    public void DecodeEraseFlashResponse_WithEmptyData_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse([]));
    }

    [Fact]
    public void DecodeEraseFlashResponse_WithoutSohStart_ReturnsFalse()
    {
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse([0x00, 0x02]));
    }

    [Fact]
    public void DecodeEraseFlashResponse_ValidResponse_ReturnsTrue()
    {
        // SOH + EraseFlashCommand(0x02)
        Assert.True(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse([SOH, 0x02]));
    }

    [Fact]
    public void DecodeEraseFlashResponse_WrongCommand_ReturnsFalse()
    {
        // SOH + ProgramFlashCommand(0x03)
        Assert.False(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse([SOH, 0x03]));
    }

    #endregion
}
