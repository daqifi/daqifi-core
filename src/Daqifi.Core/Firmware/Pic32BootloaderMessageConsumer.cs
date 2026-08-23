using System.IO;
using static Daqifi.Core.Firmware.Pic32BootloaderWireFormat;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Decodes PIC32 bootloader protocol response messages.
/// Handles SOH framing validation and DLE-unescaping.
/// </summary>
/// <remarks>
/// The framing bytes and opcodes matched here come from <see cref="Pic32BootloaderWireFormat"/>,
/// shared with <see cref="Pic32BootloaderMessageProducer"/> so the two sides cannot drift apart.
/// </remarks>
public static class Pic32BootloaderMessageConsumer
{
    /// <summary>
    /// Decodes a version response from the bootloader.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>A version string in "Major.Minor" format, or "Error" if the response is invalid.</returns>
    public static string DecodeVersionResponse(byte[] data)
    {
        var majorVersion = 0;
        var minorVersion = 0;

        if (data.Length < 2) return "Error";

        if (data[0] != StartOfHeader) return "Error";

        // The command byte (0x01) matches SOH, so it will be DLE-escaped.
        // Minimum valid version response: SOH + DLE + cmd + major + minor = 5 bytes
        // With DLE-escaped version bytes it can be up to 7 bytes
        if (data.Length >= 5 && data[1] == DataLinkEscape && data[2] == RequestVersionCommand)
        {
            var pointer = 3;

            if (pointer < data.Length)
            {
                majorVersion = data[pointer] == DataLinkEscape && pointer + 1 < data.Length ? data[++pointer] : data[pointer];
                pointer++;
            }

            if (pointer < data.Length)
            {
                minorVersion = data[pointer] == DataLinkEscape && pointer + 1 < data.Length ? data[++pointer] : data[pointer];
            }
        }

        return $"{majorVersion}.{minorVersion}";
    }

    /// <summary>
    /// Decodes a program flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid program flash acknowledgment.</returns>
    public static bool DecodeProgramFlashResponse(byte[] data) => IsAckFor(data, ProgramFlashCommand);

    /// <summary>
    /// Decodes an erase flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid erase flash acknowledgment.</returns>
    public static bool DecodeEraseFlashResponse(byte[] data) => IsAckFor(data, EraseFlashCommand);

    /// <summary>
    /// Decodes a <c>READ_CRC</c> response and returns the flash CRC-16 the
    /// bootloader computed. The wire frame is
    /// <c>SOH DLE 0x04 &lt;crcLo&gt; &lt;crcHi&gt; &lt;frameCrcLo&gt; &lt;frameCrcHi&gt; EOT</c>,
    /// with any of the payload bytes DLE-escaped when they collide with SOH/EOT/DLE.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <returns>The 16-bit CRC reported by the bootloader.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the response is malformed, is not a <c>READ_CRC</c> response,
    /// or fails its framing-CRC integrity check.
    /// </exception>
    public static ushort DecodeReadCrcResponse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 2 || data[0] != StartOfHeader)
        {
            throw new InvalidDataException("READ_CRC response did not begin with SOH framing.");
        }

        // Unescape the body: DLE marks the next byte as literal payload; an
        // unescaped EOT terminates the frame. Yields [cmd, crcLo, crcHi,
        // frameCrcLo, frameCrcHi] (the firmware appends a framing CRC-16 over
        // the response content before framing).
        var payload = new List<byte>(5);
        var escaped = false;
        var terminated = false;
        for (var i = 1; i < data.Length; i++)
        {
            var b = data[i];
            if (escaped)
            {
                payload.Add(b);
                escaped = false;
                continue;
            }

            if (b == DataLinkEscape)
            {
                escaped = true;
                continue;
            }

            if (b == EndOfTransmission)
            {
                terminated = true;
                break;
            }

            payload.Add(b);
        }

        if (!terminated)
        {
            throw new InvalidDataException("READ_CRC response was not EOT-terminated.");
        }

        if (payload.Count < 5)
        {
            throw new InvalidDataException(
                $"READ_CRC response was too short ({payload.Count} payload byte(s); expected 5).");
        }

        if (payload[0] != ReadCrcCommand)
        {
            throw new InvalidDataException(
                $"READ_CRC response had unexpected command byte 0x{payload[0]:X2}.");
        }

        var flashCrc = (ushort)(payload[1] | (payload[2] << 8));
        var frameCrc = (ushort)(payload[3] | (payload[4] << 8));
        var expectedFrameCrc = new Crc16([payload[0], payload[1], payload[2]]).Crc;
        if (frameCrc != expectedFrameCrc)
        {
            throw new InvalidDataException(
                $"READ_CRC response framing CRC mismatch: frame=0x{frameCrc:X4}, computed=0x{expectedFrameCrc:X4}.");
        }

        return flashCrc;
    }

    /// <summary>
    /// Matches a bare acknowledgment frame &#8212; <c>SOH &lt;opcode&gt;</c>, with no payload to
    /// unescape. ERASE_FLASH and PROGRAM_FLASH are both acknowledged this way, so the only thing
    /// that tells the two acks apart is which opcode the bootloader echoed back.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <param name="command">The opcode the acknowledgment is expected to echo.</param>
    /// <returns>True if <paramref name="data"/> acknowledges <paramref name="command"/>.</returns>
    /// <remarks>
    /// Deliberately not null-guarded. Both callers are public methods that have always thrown
    /// <see cref="System.NullReferenceException"/> on a null <paramref name="data"/>, and adding a
    /// guard here would change that observable behavior. The null-handling asymmetry between the
    /// decoders on this type is tracked separately, not settled by this extraction.
    /// </remarks>
    private static bool IsAckFor(byte[] data, byte command)
    {
        if (data.Length < 2) return false;
        if (data[0] != StartOfHeader) return false;

        return data[1] == command;
    }
}
