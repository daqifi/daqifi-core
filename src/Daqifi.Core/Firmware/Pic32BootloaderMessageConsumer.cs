using System.IO;
using static Daqifi.Core.Firmware.Pic32BootloaderWireFormat;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Decodes PIC32 bootloader protocol response messages.
/// Handles SOH framing validation, DLE-unescaping and the response CRC-16 check.
/// </summary>
/// <remarks>
/// <para>
/// Every reply the bootloader sends has the same shape: <c>SOH</c>, then the response content
/// (the echoed command byte and whatever that command answers with) followed by a CRC-16 over
/// that content, all DLE-escaped, then <c>EOT</c>. The firmware builds it in one place
/// (<c>BOOTLOADER_SEND_RESPONSE</c> in <c>bootloader.c</c>), so one decoder
/// (<see cref="TryReadResponseContent"/>) checks it for all four reply decoders here.
/// </para>
/// <para>
/// The framing bytes and opcodes matched here come from <see cref="Pic32BootloaderWireFormat"/>,
/// shared with <see cref="Pic32BootloaderMessageProducer"/> so the two sides cannot drift apart.
/// The CRC is <see cref="Crc16"/>, the same routine the producer stamps outbound messages with
/// and the same 4-bit-table CRC-16 the firmware's <c>APP_CalculateCrc</c> computes.
/// </para>
/// </remarks>
public static class Pic32BootloaderMessageConsumer
{
    /// <summary>
    /// Decodes a version response from the bootloader. The wire frame is
    /// <c>SOH DLE 0x01 &lt;major&gt; &lt;minor&gt; &lt;crcLo&gt; &lt;crcHi&gt; EOT</c>, with any
    /// content byte DLE-escaped when it collides with SOH/EOT/DLE.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <returns>
    /// A version string in "Major.Minor" format, or "Error" if the response is not a complete,
    /// CRC-correct version frame. A short or damaged reply always reports "Error"; it never
    /// reports a version.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    public static string DecodeVersionResponse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Content is [cmd, major, minor]: the opcode the firmware echoes, then the two BootInfo
        // bytes it copies after it.
        return TryReadResponseContent(data, RequestVersionCommand, 3, "REQUEST_VERSION", out var content, out _)
            ? $"{content[1]}.{content[2]}"
            : "Error";
    }

    /// <summary>
    /// Decodes a program flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <returns>True if the response is a valid program flash acknowledgment.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    public static bool DecodeProgramFlashResponse(byte[] data) =>
        IsAckFor(data, ProgramFlashCommand, "PROGRAM_FLASH");

    /// <summary>
    /// Decodes an erase flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <returns>True if the response is a valid erase flash acknowledgment.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    public static bool DecodeEraseFlashResponse(byte[] data) =>
        IsAckFor(data, EraseFlashCommand, "ERASE_FLASH");

    /// <summary>
    /// Decodes a <c>READ_CRC</c> response and returns the flash CRC-16 the
    /// bootloader computed. The wire frame is
    /// <c>SOH DLE 0x04 &lt;crcLo&gt; &lt;crcHi&gt; &lt;frameCrcLo&gt; &lt;frameCrcHi&gt; EOT</c>,
    /// with any of the content bytes DLE-escaped when they collide with SOH/EOT/DLE.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <returns>The 16-bit CRC reported by the bootloader.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the response is malformed, is not a <c>READ_CRC</c> response,
    /// or fails its response-CRC integrity check.
    /// </exception>
    public static ushort DecodeReadCrcResponse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Content is [cmd, crcLo, crcHi].
        if (!TryReadResponseContent(data, ReadCrcCommand, 3, "READ_CRC", out var content, out var error))
        {
            throw new InvalidDataException(error);
        }

        return (ushort)(content[1] | (content[2] << 8));
    }

    /// <summary>
    /// Matches an acknowledgment frame. ERASE_FLASH and PROGRAM_FLASH are both acknowledged with
    /// the bare echoed opcode, so the only thing that tells the two acks apart is which opcode
    /// the bootloader echoed back.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <param name="command">The opcode the acknowledgment is expected to echo.</param>
    /// <param name="responseName">The response's firmware name, for the rejection reason.</param>
    /// <returns>True if <paramref name="data"/> acknowledges <paramref name="command"/>.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    /// <remarks>
    /// The guard lives here rather than in the two public callers so that all four decoders on
    /// this type report a null frame identically. The parameter is named <c>data</c> here too, so
    /// the <see cref="System.ArgumentException.ParamName"/> a caller sees is the name of the
    /// public parameter it actually passed.
    /// </remarks>
    private static bool IsAckFor(byte[] data, byte command, string responseName)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Content is [cmd] on its own: an ack answers with nothing but the echo.
        return TryReadResponseContent(data, command, 1, responseName, out _, out _);
    }

    /// <summary>
    /// Validates one bootloader reply frame and hands back its content bytes.
    /// </summary>
    /// <param name="data">The raw, framed response bytes.</param>
    /// <param name="expectedCommand">The opcode the frame must echo.</param>
    /// <param name="expectedContentLength">
    /// How many content bytes this reply carries, counting the echoed opcode.
    /// </param>
    /// <param name="responseName">The response's firmware name, used in the failure reason.</param>
    /// <param name="content">The unescaped content bytes, with the trailing CRC-16 removed.</param>
    /// <param name="error">The reason the frame was rejected, or null when it was accepted.</param>
    /// <returns>True when the frame is complete, CRC-correct and echoes the expected opcode.</returns>
    /// <remarks>
    /// The checks run in wire order — framing first, then integrity, then meaning — so the reason
    /// names the first thing that is actually wrong with the bytes. The CRC is verified before the
    /// opcode echo, because a corrupt byte can just as easily land on the echo as anywhere else.
    /// </remarks>
    private static bool TryReadResponseContent(
        byte[] data,
        byte expectedCommand,
        int expectedContentLength,
        string responseName,
        out byte[] content,
        out string? error)
    {
        content = [];

        if (data.Length == 0)
        {
            error = $"{responseName} response was empty.";
            return false;
        }

        if (data[0] != StartOfHeader)
        {
            error = $"{responseName} response did not begin with SOH framing.";
            return false;
        }

        // Unescape the body: DLE marks the next byte as literal content; an unescaped EOT
        // terminates the frame. Anything after that EOT is the zero padding of the HID report the
        // frame arrived in, so it is ignored.
        var body = new List<byte>(expectedContentLength + 2);
        var escaped = false;
        var terminated = false;
        for (var i = 1; i < data.Length; i++)
        {
            var b = data[i];
            if (escaped)
            {
                body.Add(b);
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

            body.Add(b);
        }

        if (!terminated)
        {
            // A truncated read lands here: the bytes that would have carried the rest of the
            // content, the CRC and the EOT never arrived.
            error = $"{responseName} response was not EOT-terminated.";
            return false;
        }

        if (body.Count < 3)
        {
            error = $"{responseName} response was too short ({body.Count} byte(s) inside the "
                    + "frame; an echoed opcode and its 2-byte CRC need at least 3).";
            return false;
        }

        var reportedCrc = (ushort)(body[^2] | (body[^1] << 8));
        var contentBytes = body.GetRange(0, body.Count - 2).ToArray();
        var computedCrc = new Crc16(contentBytes).Crc;
        if (reportedCrc != computedCrc)
        {
            error = $"{responseName} response CRC mismatch: frame=0x{reportedCrc:X4}, "
                    + $"computed=0x{computedCrc:X4}.";
            return false;
        }

        if (contentBytes[0] != expectedCommand)
        {
            error = $"{responseName} response had unexpected command byte "
                    + $"0x{contentBytes[0]:X2} (expected 0x{expectedCommand:X2}).";
            return false;
        }

        if (contentBytes.Length != expectedContentLength)
        {
            error = $"{responseName} response had {contentBytes.Length} content byte(s); "
                    + $"expected {expectedContentLength}.";
            return false;
        }

        content = contentBytes;
        error = null;
        return true;
    }
}
