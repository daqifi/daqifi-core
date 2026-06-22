using System.IO;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Decodes PIC32 bootloader protocol response messages.
/// Handles SOH framing validation and DLE-unescaping.
/// </summary>
public static class Pic32BootloaderMessageConsumer
{
    private const byte START_OF_HEADER = 0x01;
    private const byte END_OF_TRANSMISSION = 0x04;
    private const byte DATA_LINK_ESCAPE = 0x10;
    private const byte REQUEST_VERSION_COMMAND = 0x01;
    private const byte ERASE_FLASH_COMMAND = 0x02;
    private const byte PROGRAM_FLASH_COMMAND = 0x03;
    private const byte READ_CRC_COMMAND = 0x04;

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

        if (data[0] != START_OF_HEADER) return "Error";

        // The command byte (0x01) matches SOH, so it will be DLE-escaped.
        // Minimum valid version response: SOH + DLE + cmd + major + minor = 5 bytes
        // With DLE-escaped version bytes it can be up to 7 bytes
        if (data.Length >= 5 && data[1] == DATA_LINK_ESCAPE && data[2] == REQUEST_VERSION_COMMAND)
        {
            var pointer = 3;

            if (pointer < data.Length)
            {
                majorVersion = data[pointer] == DATA_LINK_ESCAPE && pointer + 1 < data.Length ? data[++pointer] : data[pointer];
                pointer++;
            }

            if (pointer < data.Length)
            {
                minorVersion = data[pointer] == DATA_LINK_ESCAPE && pointer + 1 < data.Length ? data[++pointer] : data[pointer];
            }
        }

        return $"{majorVersion}.{minorVersion}";
    }

    /// <summary>
    /// Decodes a program flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid program flash acknowledgment.</returns>
    public static bool DecodeProgramFlashResponse(byte[] data)
    {
        if (data.Length < 2) return false;
        if (data[0] != START_OF_HEADER) return false;

        return data[1] == PROGRAM_FLASH_COMMAND;
    }

    /// <summary>
    /// Decodes an erase flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid erase flash acknowledgment.</returns>
    public static bool DecodeEraseFlashResponse(byte[] data)
    {
        if (data.Length < 2) return false;
        if (data[0] != START_OF_HEADER) return false;

        return data[1] == ERASE_FLASH_COMMAND;
    }

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

        if (data.Length < 2 || data[0] != START_OF_HEADER)
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

            if (b == DATA_LINK_ESCAPE)
            {
                escaped = true;
                continue;
            }

            if (b == END_OF_TRANSMISSION)
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

        if (payload[0] != READ_CRC_COMMAND)
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
}
