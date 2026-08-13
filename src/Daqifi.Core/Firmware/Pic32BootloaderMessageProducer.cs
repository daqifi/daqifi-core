using System.Buffers.Binary;
using static Daqifi.Core.Firmware.Pic32BootloaderWireFormat;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Produces PIC32 bootloader protocol messages with SOH/EOT framing,
/// DLE byte escaping, and CRC-16 checksums.
/// </summary>
/// <remarks>
/// The framing bytes and opcodes come from <see cref="Pic32BootloaderWireFormat"/>, shared with
/// <see cref="Pic32BootloaderMessageConsumer"/> so the two sides cannot drift apart.
/// </remarks>
public static class Pic32BootloaderMessageProducer
{
    /// <summary>
    /// Creates a message to request the bootloader version.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateRequestVersionMessage()
    {
        return ConstructDataPacket(RequestVersionCommand);
    }

    /// <summary>
    /// Creates a message to erase the device flash memory.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateEraseFlashMessage()
    {
        return ConstructDataPacket(EraseFlashCommand);
    }

    /// <summary>
    /// Creates a message to program a hex record into flash memory.
    /// </summary>
    /// <param name="hexRecord">The hex record bytes to program.</param>
    /// <returns>The framed and escaped message bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hexRecord"/> is null.</exception>
    public static byte[] CreateProgramFlashMessage(byte[] hexRecord)
    {
        ArgumentNullException.ThrowIfNull(hexRecord);

        var command = new byte[1 + hexRecord.Length];
        command[0] = ProgramFlashCommand;
        Array.Copy(hexRecord, 0, command, 1, hexRecord.Length);
        return ConstructDataPacket(command);
    }

    /// <summary>
    /// Creates a <c>READ_CRC</c> message asking the bootloader to checksum a
    /// flash region. The payload is the command byte followed by the 4-byte
    /// little-endian address and 4-byte little-endian length the firmware reads
    /// from <c>buff2[1..4]</c> and <c>buff2[5..8]</c>.
    /// </summary>
    /// <param name="address">KSEG0 virtual flash address of the first byte.</param>
    /// <param name="length">Number of contiguous flash bytes to checksum.</param>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateReadCrcMessage(uint address, uint length)
    {
        var command = new byte[9];
        command[0] = ReadCrcCommand;
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(1, 4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(5, 4), length);
        return ConstructDataPacket(command);
    }

    /// <summary>
    /// Creates a message to jump from the bootloader to the application.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateJumpToApplicationMessage()
    {
        return ConstructDataPacket(JumpToApplicationCommand);
    }

    private static byte[] ConstructDataPacket(byte command)
    {
        return ConstructDataPacket([command]);
    }

    private static byte[] ConstructDataPacket(byte[] command)
    {
        var packet = new List<byte>();
        var crc = new Crc16(command);

        var commandAndCrc = new byte[command.Length + 2];
        Array.Copy(command, commandAndCrc, command.Length);
        commandAndCrc[command.Length] = crc.Low;
        commandAndCrc[command.Length + 1] = crc.High;

        packet.Add(StartOfHeader);

        foreach (var item in commandAndCrc)
        {
            if (item is StartOfHeader or EndOfTransmission or DataLinkEscape)
            {
                packet.Add(DataLinkEscape);
            }
            packet.Add(item);
        }

        packet.Add(EndOfTransmission);
        return packet.ToArray();
    }
}
