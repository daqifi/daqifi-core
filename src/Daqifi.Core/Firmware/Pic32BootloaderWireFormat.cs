namespace Daqifi.Core.Firmware;

/// <summary>
/// Wire-format constants for the PIC32 bootloader protocol — the SOH/EOT framing bytes, the DLE
/// escape byte, and the command opcodes shared by
/// <see cref="Pic32BootloaderMessageProducer"/> and <see cref="Pic32BootloaderMessageConsumer"/>.
/// </summary>
/// <remarks>
/// <para>
/// These values are the wire contract with the device's bootloader, so the producer that writes
/// them and the consumer that matches them off the wire must agree byte for byte. A disagreement
/// between the two would not fail loudly — it would mis-frame a live flash session on real
/// hardware. Declaring them once is what makes that disagreement impossible to introduce.
/// </para>
/// <para>
/// The authority for the values is the DAQiFi firmware's own bootloader, not this file. Nothing
/// here may be changed to "fix" a decode problem: a mismatch means the firmware moved, and the
/// pinning tests in <c>Pic32BootloaderWireFormatTests</c> exist so that such a change has to be
/// deliberate and visible rather than quiet.
/// </para>
/// </remarks>
internal static class Pic32BootloaderWireFormat
{
    /// <summary>Start-of-header byte that opens every framed message in both directions.</summary>
    internal const byte StartOfHeader = 0x01;

    /// <summary>End-of-transmission byte that closes every framed message in both directions.</summary>
    internal const byte EndOfTransmission = 0x04;

    /// <summary>
    /// Escape byte. A payload byte that collides with <see cref="StartOfHeader"/>,
    /// <see cref="EndOfTransmission"/>, or this byte is preceded by it and taken literally.
    /// </summary>
    internal const byte DataLinkEscape = 0x10;

    /// <summary>
    /// Opcode asking the bootloader for its version. Note that it collides with
    /// <see cref="StartOfHeader"/>, so it is always DLE-escaped on the wire.
    /// </summary>
    internal const byte RequestVersionCommand = 0x01;

    /// <summary>Opcode erasing the application flash partition.</summary>
    internal const byte EraseFlashCommand = 0x02;

    /// <summary>Opcode programming one HEX record into flash; the record follows the opcode.</summary>
    internal const byte ProgramFlashCommand = 0x03;

    /// <summary>
    /// Opcode asking the bootloader to checksum a flash region; a 4-byte little-endian address and
    /// a 4-byte little-endian length follow the opcode.
    /// </summary>
    internal const byte ReadCrcCommand = 0x04;

    /// <summary>
    /// Opcode telling the bootloader to jump to the application. Producer-only — the device leaves
    /// the bootloader rather than answering, so there is no response to decode.
    /// </summary>
    internal const byte JumpToApplicationCommand = 0x05;
}
