namespace Daqifi.Core.Firmware;

/// <summary>
/// Implements the PIC32 bootloader protocol, delegating to the static
/// message producer/consumer and HEX parser classes.
/// All operations are transport-agnostic.
/// </summary>
public class Pic32BootloaderProtocol : IBootloaderProtocol
{
    private readonly IntelHexParser _hexParser;

    /// <summary>
    /// Creates a new instance with the default protected memory range.
    /// </summary>
    public Pic32BootloaderProtocol()
    {
        _hexParser = new IntelHexParser();
    }

    /// <summary>
    /// Creates a new instance with a custom protected memory range.
    /// </summary>
    /// <param name="beginProtectedAddress">Start of the protected memory range.</param>
    /// <param name="endProtectedAddress">End of the protected memory range.</param>
    public Pic32BootloaderProtocol(uint beginProtectedAddress, uint endProtectedAddress)
    {
        _hexParser = new IntelHexParser(beginProtectedAddress, endProtectedAddress);
    }

    /// <inheritdoc />
    public byte[] CreateRequestVersionMessage()
    {
        return Pic32BootloaderMessageProducer.CreateRequestVersionMessage();
    }

    /// <inheritdoc />
    public byte[] CreateEraseFlashMessage()
    {
        return Pic32BootloaderMessageProducer.CreateEraseFlashMessage();
    }

    /// <inheritdoc />
    public byte[] CreateProgramFlashMessage(byte[] hexRecord)
    {
        return Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);
    }

    /// <inheritdoc />
    public byte[] CreateJumpToApplicationMessage()
    {
        return Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage();
    }

    /// <inheritdoc />
    public string DecodeVersionResponse(byte[] data)
    {
        return Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);
    }

    /// <inheritdoc />
    public bool DecodeProgramFlashResponse(byte[] data)
    {
        return Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(data);
    }

    /// <inheritdoc />
    public bool DecodeEraseFlashResponse(byte[] data)
    {
        return Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(data);
    }

    /// <inheritdoc />
    public List<byte[]> ParseHexFile(string[] hexFileLines)
    {
        return _hexParser.ParseHexRecords(hexFileLines);
    }
}
