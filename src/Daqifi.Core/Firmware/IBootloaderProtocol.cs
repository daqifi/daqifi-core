namespace Daqifi.Core.Firmware;

/// <summary>
/// Defines the PIC32 bootloader protocol operations.
/// All operations are transport-agnostic, producing and consuming <c>byte[]</c>.
/// </summary>
public interface IBootloaderProtocol
{
    /// <summary>
    /// Creates a message to request the bootloader version.
    /// </summary>
    /// <returns>The framed message bytes ready for transmission.</returns>
    byte[] CreateRequestVersionMessage();

    /// <summary>
    /// Creates a message to erase the device flash memory.
    /// </summary>
    /// <returns>The framed message bytes ready for transmission.</returns>
    byte[] CreateEraseFlashMessage();

    /// <summary>
    /// Creates a message to program a hex record into flash memory.
    /// </summary>
    /// <param name="hexRecord">The hex record bytes to program.</param>
    /// <returns>The framed message bytes ready for transmission.</returns>
    byte[] CreateProgramFlashMessage(byte[] hexRecord);

    /// <summary>
    /// Creates a message to jump from the bootloader to the application.
    /// </summary>
    /// <returns>The framed message bytes ready for transmission.</returns>
    byte[] CreateJumpToApplicationMessage();

    /// <summary>
    /// Decodes a version response from the bootloader.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>A version string in "Major.Minor" format, or "Error" if the response is invalid.</returns>
    string DecodeVersionResponse(byte[] data);

    /// <summary>
    /// Decodes a program flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid program flash acknowledgment.</returns>
    bool DecodeProgramFlashResponse(byte[] data);

    /// <summary>
    /// Decodes an erase flash acknowledgment response.
    /// </summary>
    /// <param name="data">The raw response bytes.</param>
    /// <returns>True if the response is a valid erase flash acknowledgment.</returns>
    bool DecodeEraseFlashResponse(byte[] data);

    /// <summary>
    /// Parses an Intel HEX file into raw hex record byte arrays, filtering out
    /// records in the protected memory range.
    /// </summary>
    /// <param name="hexFileLines">The lines from the HEX file.</param>
    /// <returns>A list of byte arrays, each representing one hex record.</returns>
    List<byte[]> ParseHexFile(string[] hexFileLines);
}
