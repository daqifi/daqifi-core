namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// Wire-format constants and framing for the WINC serial bridge — the UART protocol the DAQiFi
/// PIC32 speaks while it is bridging its USB-CDC port through to the WINC1500.
/// </summary>
/// <remarks>
/// <para>
/// The authority for this format is the DAQiFi firmware's own bridge implementation
/// (<c>firmware/src/services/wifi_services/wifi_serial_bridge.c</c>), not Microchip's tool —
/// Microchip ships <c>winc_programmer_uart</c> as a binary only, with no source, so the bridge
/// the device actually implements is the better and more accurate reference.
/// </para>
/// <para>
/// A command is an <see cref="StartCommand"/> byte followed by a 12-byte header whose bytes XOR to
/// zero. Note the mixed endianness, which is easy to get wrong: header fields are little-endian,
/// but a register value read back by <see cref="Command.ReadRegisterWithReturn"/> arrives
/// big-endian.
/// </para>
/// </remarks>
internal static class WincBridgeProtocol
{
    /// <summary>Size of the fixed command header that follows <see cref="StartCommand"/>.</summary>
    internal const int HeaderSize = 12;

    /// <summary>Identify op code; the bridge answers <see cref="Response.IdVariableBaud"/>.</summary>
    internal const byte IdentifyVariableBaud = 0x12;

    /// <summary>
    /// Identify op code for a fixed-baud bridge. The DAQiFi bridge is variable-baud and answers
    /// nothing at all to this, so it is only useful for distinguishing bridge flavors.
    /// </summary>
    internal const byte IdentifyFixedBaud = 0x13;

    /// <summary>Op code that begins a command; the next <see cref="HeaderSize"/> bytes are the header.</summary>
    internal const byte StartCommand = 0xA5;

    /// <summary>
    /// Largest payload a single <see cref="Command.ReadBlock"/> may request.
    /// </summary>
    /// <remarks>
    /// The bridge's read loop neither decrements its counter nor advances the address, so a request
    /// of 2048 or more spins forever re-sending the same chunk and never returns. Reads must
    /// therefore stay strictly below the firmware's 2048-byte command buffer. This cap is a
    /// deliberate guard against that firmware behavior, not a protocol limit.
    /// </remarks>
    internal const int MaxReadBlockSize = 2047;

    /// <summary>Largest payload a single <see cref="Command.WriteBlock"/> may carry.</summary>
    internal const int MaxWriteBlockSize = 2048;

    /// <summary>Bridge command identifiers (header byte 0).</summary>
    internal enum Command : byte
    {
        /// <summary>Read a 32-bit register; the bridge returns 4 big-endian bytes.</summary>
        ReadRegisterWithReturn = 0,

        /// <summary>Write a 32-bit register. No data response beyond the header ACK.</summary>
        WriteRegister = 1,

        /// <summary>Read <c>size</c> raw bytes starting at <c>address</c>.</summary>
        ReadBlock = 2,

        /// <summary>Write <c>size</c> payload bytes, sent after the header ACK.</summary>
        WriteBlock = 3,

        /// <summary>Re-negotiate the bridge's UART baud rate to <c>value</c>.</summary>
        Reconfigure = 5
    }

    /// <summary>Single-byte responses the bridge emits.</summary>
    internal static class Response
    {
        /// <summary>Header checksum rejected, or a block write failed.</summary>
        internal const byte Nack = 0x5A;

        /// <summary>Identify response from a variable-baud bridge.</summary>
        internal const byte IdVariableBaud = 0x5B;

        /// <summary>Identify response from a fixed-baud bridge.</summary>
        internal const byte IdFixedBaud = 0x5C;

        /// <summary>Header accepted, or a block write succeeded.</summary>
        internal const byte Ack = 0xAC;
    }

    /// <summary>
    /// Builds the 12-byte command header. Header fields are little-endian; byte 1 is the checksum
    /// slot, set so the XOR of all twelve bytes is zero.
    /// </summary>
    /// <param name="command">The command identifier.</param>
    /// <param name="size">Payload/transfer size (little-endian u16).</param>
    /// <param name="address">Target address (little-endian u32).</param>
    /// <param name="value">Command value — the register value, or the new baud rate (little-endian u32).</param>
    internal static byte[] BuildHeader(Command command, ushort size, uint address, uint value)
    {
        var header = new byte[HeaderSize];

        header[0] = (byte)command;
        // header[1] is the checksum, filled in below.
        header[2] = (byte)(size & 0xFF);
        header[3] = (byte)((size >> 8) & 0xFF);
        header[4] = (byte)(address & 0xFF);
        header[5] = (byte)((address >> 8) & 0xFF);
        header[6] = (byte)((address >> 16) & 0xFF);
        header[7] = (byte)((address >> 24) & 0xFF);
        header[8] = (byte)(value & 0xFF);
        header[9] = (byte)((value >> 8) & 0xFF);
        header[10] = (byte)((value >> 16) & 0xFF);
        header[11] = (byte)((value >> 24) & 0xFF);

        header[1] = ComputeChecksum(header);
        return header;
    }

    /// <summary>
    /// Returns the byte that, placed at index 1, makes the header's full XOR zero. Computed by
    /// XOR-ing every byte except index 1 itself.
    /// </summary>
    internal static byte ComputeChecksum(IReadOnlyList<byte> header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (header.Count != HeaderSize)
        {
            throw new ArgumentException(
                $"A bridge header is exactly {HeaderSize} bytes; got {header.Count}.", nameof(header));
        }

        byte checksum = 0;
        for (var i = 0; i < HeaderSize; i++)
        {
            if (i == 1)
            {
                continue;
            }

            checksum ^= header[i];
        }

        return checksum;
    }

    /// <summary>
    /// True when the header satisfies the bridge's acceptance test: the XOR of all twelve bytes is
    /// zero. This is the exact check the firmware performs before it ACKs.
    /// </summary>
    internal static bool IsHeaderValid(IReadOnlyList<byte> header)
    {
        if (header is null || header.Count != HeaderSize)
        {
            return false;
        }

        byte checksum = 0;
        for (var i = 0; i < HeaderSize; i++)
        {
            checksum ^= header[i];
        }

        return checksum == 0;
    }

    /// <summary>
    /// Decodes a register value from a <see cref="Command.ReadRegisterWithReturn"/> reply, which the
    /// bridge sends big-endian — the opposite order from the header fields.
    /// </summary>
    internal static uint DecodeRegisterValue(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length != 4)
        {
            throw new ArgumentException(
                $"A register response is exactly 4 bytes; got {response.Length}.", nameof(response));
        }

        return ((uint)response[0] << 24)
             | ((uint)response[1] << 16)
             | ((uint)response[2] << 8)
             | response[3];
    }
}
