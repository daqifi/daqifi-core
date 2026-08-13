using System.Reflection;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Locks down the single-sourced PIC32 bootloader wire format two ways: the byte values are
/// pinned against a silent edit, and the opcode the producer sends is shown to be the opcode the
/// consumer expects back — without either class's copy of the value being written down here.
/// </summary>
public class Pic32BootloaderWireFormatTests
{
    #region Pinned values

    // The literals below are restated on purpose. Now that the producer and the consumer read the
    // same constants, editing those constants no longer breaks anything by disagreeing with the
    // other side — it just changes what Core puts on the wire, in every flash session, quietly.
    // This file is the second copy that has to be edited too, and the firmware's own bootloader
    // is the authority for what these values are allowed to be.

    [Fact]
    public void FramingBytes_AreTheValuesTheFirmwareFramesWith()
    {
        Assert.Equal(0x01, Pic32BootloaderWireFormat.StartOfHeader);
        Assert.Equal(0x04, Pic32BootloaderWireFormat.EndOfTransmission);
        Assert.Equal(0x10, Pic32BootloaderWireFormat.DataLinkEscape);
    }

    [Fact]
    public void CommandOpcodes_AreTheValuesTheFirmwareDispatchesOn()
    {
        Assert.Equal(0x01, Pic32BootloaderWireFormat.RequestVersionCommand);
        Assert.Equal(0x02, Pic32BootloaderWireFormat.EraseFlashCommand);
        Assert.Equal(0x03, Pic32BootloaderWireFormat.ProgramFlashCommand);
        Assert.Equal(0x04, Pic32BootloaderWireFormat.ReadCrcCommand);
        Assert.Equal(0x05, Pic32BootloaderWireFormat.JumpToApplicationCommand);
    }

    [Fact]
    public void RequestVersionOpcode_CollidesWithStartOfHeader_WhichIsWhyItIsAlwaysEscaped()
    {
        // Not a coincidence to be tidied away: the version decode path depends on the opcode
        // arriving DLE-escaped precisely because it is the same byte as SOH.
        Assert.Equal(Pic32BootloaderWireFormat.StartOfHeader, Pic32BootloaderWireFormat.RequestVersionCommand);
    }

    [Fact]
    public void ReadCrcOpcode_CollidesWithEndOfTransmission_WhichIsWhyItIsAlwaysEscaped()
    {
        Assert.Equal(Pic32BootloaderWireFormat.EndOfTransmission, Pic32BootloaderWireFormat.ReadCrcCommand);
    }

    #endregion

    #region Producer/consumer agreement

    // No opcode is written down in these tests. Each one takes the opcode out of a message the
    // producer actually built and hands it to the consumer, so they fail if either class ever
    // goes back to declaring its own copy of an opcode.

    [Fact]
    public void EraseFlashAcknowledgment_UsesTheSameOpcodeTheProducerSent()
    {
        var sent = Pic32BootloaderMessageProducer.CreateEraseFlashMessage();

        var acknowledgment = new[] { sent[0], OpcodeOf(sent) };

        Assert.True(Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(acknowledgment));
    }

    [Fact]
    public void ProgramFlashAcknowledgment_UsesTheSameOpcodeTheProducerSent()
    {
        var sent = Pic32BootloaderMessageProducer.CreateProgramFlashMessage([0x08, 0x00, 0x20, 0x00, 0xAA]);

        var acknowledgment = new[] { sent[0], OpcodeOf(sent) };

        Assert.True(Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(acknowledgment));
    }

    [Fact]
    public void VersionResponse_UsesTheSameOpcodeTheProducerSent()
    {
        var sent = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();

        // The version response is the one frame the consumer unescapes inline: SOH, DLE, opcode,
        // major, minor.
        var response = new[]
        {
            sent[0], Pic32BootloaderWireFormat.DataLinkEscape, OpcodeOf(sent), (byte)2, (byte)5
        };

        Assert.Equal("2.5", Pic32BootloaderMessageConsumer.DecodeVersionResponse(response));
    }

    [Fact]
    public void ReadCrcResponse_UsesTheSameOpcodeTheProducerSent()
    {
        var sent = Pic32BootloaderMessageProducer.CreateReadCrcMessage(0x9D000000, 0x200000);
        const ushort flashCrc = 0xABCD;

        // The firmware echoes the opcode, then the flash CRC, then a framing CRC over those three
        // bytes; the whole body is then SOH/EOT-framed with escaping.
        byte[] content = [OpcodeOf(sent), (byte)(flashCrc & 0xFF), (byte)(flashCrc >> 8)];
        var frameCrc = new Crc16(content).Crc;
        var response = Frame([.. content, (byte)(frameCrc & 0xFF), (byte)(frameCrc >> 8)]);

        Assert.Equal(flashCrc, Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(response));
    }

    #endregion

    #region Single-declaration guard

    [Theory]
    [InlineData(typeof(Pic32BootloaderMessageProducer))]
    [InlineData(typeof(Pic32BootloaderMessageConsumer))]
    public void MessageClasses_DeclareNoWireConstantsOfTheirOwn(Type messageClass)
    {
        var declared = messageClass
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(declared);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The command opcode inside a message the producer built: the first payload byte, once the
    /// leading SOH is dropped and DLE escaping is undone.
    /// </summary>
    private static byte OpcodeOf(byte[] producedMessage)
    {
        var payload = new List<byte>();
        var escaped = false;
        for (var i = 1; i < producedMessage.Length; i++)
        {
            var b = producedMessage[i];
            if (escaped)
            {
                payload.Add(b);
                escaped = false;
                continue;
            }

            if (b == Pic32BootloaderWireFormat.DataLinkEscape)
            {
                escaped = true;
                continue;
            }

            if (b == Pic32BootloaderWireFormat.EndOfTransmission)
            {
                break;
            }

            payload.Add(b);
        }

        Assert.NotEmpty(payload);
        return payload[0];
    }

    /// <summary>SOH/EOT-frames a response body with DLE escaping, the way the firmware does.</summary>
    private static byte[] Frame(byte[] body)
    {
        var framed = new List<byte> { Pic32BootloaderWireFormat.StartOfHeader };
        foreach (var b in body)
        {
            if (b is Pic32BootloaderWireFormat.StartOfHeader
                  or Pic32BootloaderWireFormat.EndOfTransmission
                  or Pic32BootloaderWireFormat.DataLinkEscape)
            {
                framed.Add(Pic32BootloaderWireFormat.DataLinkEscape);
            }
            framed.Add(b);
        }
        framed.Add(Pic32BootloaderWireFormat.EndOfTransmission);
        return framed.ToArray();
    }

    #endregion
}
