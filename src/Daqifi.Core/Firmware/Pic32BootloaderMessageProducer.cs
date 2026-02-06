namespace Daqifi.Core.Firmware;

/// <summary>
/// Produces PIC32 bootloader protocol messages with SOH/EOT framing,
/// DLE byte escaping, and CRC-16 checksums.
/// </summary>
public static class Pic32BootloaderMessageProducer
{
    private const byte START_OF_HEADER = 0x01;
    private const byte END_OF_TRANSMISSION = 0x04;
    private const byte DATA_LINK_ESCAPE = 0x10;
    private const byte REQUEST_VERSION_COMMAND = 0x01;
    private const byte ERASE_FLASH_COMMAND = 0x02;
    private const byte PROGRAM_FLASH_COMMAND = 0x03;
    private const byte JUMP_TO_APPLICATION_COMMAND = 0x05;

    /// <summary>
    /// Creates a message to request the bootloader version.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateRequestVersionMessage()
    {
        return ConstructDataPacket(REQUEST_VERSION_COMMAND);
    }

    /// <summary>
    /// Creates a message to erase the device flash memory.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateEraseFlashMessage()
    {
        return ConstructDataPacket(ERASE_FLASH_COMMAND);
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
        command[0] = PROGRAM_FLASH_COMMAND;
        Array.Copy(hexRecord, 0, command, 1, hexRecord.Length);
        return ConstructDataPacket(command);
    }

    /// <summary>
    /// Creates a message to jump from the bootloader to the application.
    /// </summary>
    /// <returns>The framed and escaped message bytes.</returns>
    public static byte[] CreateJumpToApplicationMessage()
    {
        return ConstructDataPacket(JUMP_TO_APPLICATION_COMMAND);
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

        packet.Add(START_OF_HEADER);

        foreach (var item in commandAndCrc)
        {
            if (item is START_OF_HEADER or END_OF_TRANSMISSION or DATA_LINK_ESCAPE)
            {
                packet.Add(DATA_LINK_ESCAPE);
            }
            packet.Add(item);
        }

        packet.Add(END_OF_TRANSMISSION);
        return packet.ToArray();
    }
}
