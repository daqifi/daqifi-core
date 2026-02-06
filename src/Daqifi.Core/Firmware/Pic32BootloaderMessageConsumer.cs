namespace Daqifi.Core.Firmware;

/// <summary>
/// Decodes PIC32 bootloader protocol response messages.
/// Handles SOH framing validation and DLE-unescaping.
/// </summary>
public static class Pic32BootloaderMessageConsumer
{
    private const byte START_OF_HEADER = 0x01;
    private const byte DATA_LINK_ESCAPE = 0x10;
    private const byte REQUEST_VERSION_COMMAND = 0x01;
    private const byte ERASE_FLASH_COMMAND = 0x02;
    private const byte PROGRAM_FLASH_COMMAND = 0x03;

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

        // The command byte (0x01) matches SOH, so it will be DLE-escaped
        if (data[1] == DATA_LINK_ESCAPE && data[2] == REQUEST_VERSION_COMMAND)
        {
            var pointer = 3;

            majorVersion = data[pointer] == DATA_LINK_ESCAPE ? data[++pointer] : data[pointer];
            pointer++;
            minorVersion = data[pointer] == DATA_LINK_ESCAPE ? data[++pointer] : data[pointer];
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
}
