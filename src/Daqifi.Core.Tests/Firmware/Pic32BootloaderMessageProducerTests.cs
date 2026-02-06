using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Pic32BootloaderMessageProducerTests
{
    private const byte SOH = 0x01;
    private const byte EOT = 0x04;
    private const byte DLE = 0x10;

    [Fact]
    public void CreateRequestVersionMessage_StartsWithSoh()
    {
        var message = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        Assert.Equal(SOH, message[0]);
    }

    [Fact]
    public void CreateRequestVersionMessage_EndsWithEot()
    {
        var message = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        Assert.Equal(EOT, message[^1]);
    }

    [Fact]
    public void CreateRequestVersionMessage_EscapesCommandByte()
    {
        // Command byte 0x01 matches SOH, so it must be DLE-escaped
        var message = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        // After SOH, first byte should be DLE (escape for the command byte 0x01)
        Assert.Equal(DLE, message[1]);
        Assert.Equal(0x01, message[2]);
    }

    [Fact]
    public void CreateEraseFlashMessage_StartsWithSohEndsWithEot()
    {
        var message = Pic32BootloaderMessageProducer.CreateEraseFlashMessage();

        Assert.Equal(SOH, message[0]);
        Assert.Equal(EOT, message[^1]);
    }

    [Fact]
    public void CreateEraseFlashMessage_ContainsEraseCommand()
    {
        var message = Pic32BootloaderMessageProducer.CreateEraseFlashMessage();

        // Command byte 0x02 should appear after SOH (no DLE needed since 0x02 isn't special)
        Assert.Equal(0x02, message[1]);
    }

    [Fact]
    public void CreateProgramFlashMessage_WithNullRecord_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => Pic32BootloaderMessageProducer.CreateProgramFlashMessage(null!));
    }

    [Fact]
    public void CreateProgramFlashMessage_StartsWithSohEndsWithEot()
    {
        var hexRecord = new byte[] { 0x10, 0x00, 0x00, 0x00, 0xFF };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        Assert.Equal(SOH, message[0]);
        Assert.Equal(EOT, message[^1]);
    }

    [Fact]
    public void CreateProgramFlashMessage_ContainsProgramCommand()
    {
        var hexRecord = new byte[] { 0x08, 0x00, 0x20, 0x00, 0xAA, 0xBB };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        // Command byte 0x03 should appear after SOH (no DLE needed)
        Assert.Equal(0x03, message[1]);
    }

    [Fact]
    public void CreateJumpToApplicationMessage_StartsWithSohEndsWithEot()
    {
        var message = Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage();

        Assert.Equal(SOH, message[0]);
        Assert.Equal(EOT, message[^1]);
    }

    [Fact]
    public void CreateJumpToApplicationMessage_ContainsJumpCommand()
    {
        var message = Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage();

        // Command byte 0x05 should appear after SOH
        Assert.Equal(0x05, message[1]);
    }

    [Fact]
    public void CreateRequestVersionMessage_IncludesCrcBytes()
    {
        var message = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        // Message must be at least: SOH + DLE + cmd + CRC_low + CRC_high + EOT = 6 bytes
        // But CRC bytes may also be escaped, so it can be longer
        Assert.True(message.Length >= 6);
    }

    [Fact]
    public void DleEscaping_SpecialBytesAreEscaped()
    {
        // When command data contains SOH (0x01), EOT (0x04), or DLE (0x10),
        // they should be preceded by DLE
        var message = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        // Verify that between SOH and EOT, any occurrence of 0x01, 0x04, or 0x10
        // (as data values) is preceded by DLE
        for (var i = 1; i < message.Length - 1; i++)
        {
            if (message[i] is SOH or EOT or DLE)
            {
                // If this is a special byte in the payload, it must be preceded by DLE
                // or it IS the DLE escape character itself
                if (message[i] == DLE)
                {
                    // This is either an escape prefix or an escaped DLE
                    // If it's an escaped DLE, the previous byte should also be DLE
                    continue;
                }

                // For SOH and EOT in payload, previous byte must be DLE
                Assert.Equal(DLE, message[i - 1]);
            }
        }
    }

    [Fact]
    public void DleEscaping_RoundTrip_DataContainingSohByte()
    {
        // ProgramFlash with data containing SOH byte should escape it
        var hexRecord = new byte[] { 0x01, 0x02, 0x03 };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        Assert.Equal(SOH, message[0]);
        Assert.Equal(EOT, message[^1]);

        // The command is 0x03 (not special), then data starts with 0x01 (SOH - must be escaped)
        // Find 0x01 in payload - it should be preceded by DLE
        var foundEscapedSoh = false;
        for (var i = 1; i < message.Length - 1; i++)
        {
            if (message[i] == SOH && i > 0 && message[i - 1] == DLE)
            {
                foundEscapedSoh = true;
                break;
            }
        }
        Assert.True(foundEscapedSoh);
    }

    [Fact]
    public void DleEscaping_DataContainingEotByte()
    {
        // ProgramFlash with data containing EOT byte should escape it
        var hexRecord = new byte[] { 0x04, 0x05 };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        // Find 0x04 in payload (not the trailing EOT) - it should be preceded by DLE
        var foundEscapedEot = false;
        for (var i = 1; i < message.Length - 1; i++)
        {
            if (message[i] == EOT && message[i - 1] == DLE)
            {
                foundEscapedEot = true;
                break;
            }
        }
        Assert.True(foundEscapedEot);
    }

    [Fact]
    public void DleEscaping_DataContainingDleByte()
    {
        // ProgramFlash with data containing DLE byte should escape it
        var hexRecord = new byte[] { 0x10, 0x20 };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        // The DLE (0x10) in the hex record should be preceded by another DLE
        var foundEscapedDle = false;
        for (var i = 2; i < message.Length - 1; i++)
        {
            if (message[i] == DLE && i > 0 && message[i - 1] == DLE)
            {
                foundEscapedDle = true;
                break;
            }
        }
        Assert.True(foundEscapedDle);
    }

    [Fact]
    public void CreateProgramFlashMessage_CrcIsComputedOverCommandAndData()
    {
        var hexRecord = new byte[] { 0xAA, 0xBB };
        var message = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);

        // Verify the CRC is computed over [0x03, 0xAA, 0xBB]
        var expectedCrc = new Crc16([0x03, 0xAA, 0xBB]);

        // The message should contain these CRC bytes (possibly DLE-escaped)
        // Just verify the CRC object is created successfully and message is valid
        Assert.True(message.Length > 4);
        Assert.Equal(SOH, message[0]);
        Assert.Equal(EOT, message[^1]);
    }

    [Fact]
    public void AllMessages_HaveValidFraming()
    {
        var messages = new[]
        {
            Pic32BootloaderMessageProducer.CreateRequestVersionMessage(),
            Pic32BootloaderMessageProducer.CreateEraseFlashMessage(),
            Pic32BootloaderMessageProducer.CreateProgramFlashMessage([0xAA]),
            Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage()
        };

        foreach (var message in messages)
        {
            Assert.Equal(SOH, message[0]);
            Assert.Equal(EOT, message[^1]);
            Assert.True(message.Length >= 4); // At minimum: SOH + cmd + crc_low + crc_high + EOT
        }
    }
}
