namespace Daqifi.Core.Firmware;

/// <summary>
/// Implements the PIC32 bootloader protocol, delegating to the static
/// message producer/consumer and HEX parser classes.
/// All operations are transport-agnostic.
/// </summary>
public class Pic32BootloaderProtocol : IBootloaderProtocol
{
    // PIC32 KSEG0 mask (PA_TO_KVA0): the bootloader programs records at
    // PA_TO_KVA0(physicalAddress) and READ_CRC reads at KVA0_TO_KVA1(address),
    // so the address we hand to READ_CRC must be the KSEG0 form of the HEX
    // record's physical address (physical | 0x80000000).
    private const uint Kseg0Mask = 0x80000000;
    private const byte DataRecordType = 0x00;

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
    public byte[] CreateReadCrcMessage(uint address, uint length)
    {
        return Pic32BootloaderMessageProducer.CreateReadCrcMessage(address, length);
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
    public ushort DecodeReadCrcResponse(byte[] data)
    {
        return Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(data);
    }

    /// <inheritdoc />
    public List<byte[]> ParseHexFile(string[] hexFileLines)
    {
        return _hexParser.ParseHexRecords(hexFileLines);
    }

    /// <inheritdoc />
    public IReadOnlyList<FlashCrcRegion> ComputeCrcRegions(string[] hexFileLines)
    {
        // Order data records by physical address so adjacent records coalesce
        // regardless of file order. The device computes its flash CRC over the
        // address-ordered bytes in a region, so this matches what READ_CRC will
        // return. Protected-range records are already excluded by ParseRecords,
        // exactly as they are excluded from programming.
        var dataRecords = _hexParser.ParseRecords(hexFileLines)
            .Where(r => r.RecordType == DataRecordType
                        && r.Data.Length > 0
                        && r.Data[0] > 0
                        && r.Data.Length >= r.Data[0] + 5)
            .OrderBy(r => r.Address)
            .ToList();

        var regions = new List<FlashCrcRegion>();
        var buffer = new List<byte>();
        uint regionStart = 0;
        uint nextAddress = 0;

        foreach (var record in dataRecords)
        {
            int dataLen = record.Data[0];

            // A gap (or, defensively, an overlap) ends the current contiguous
            // run; flush it and start a new region at this record's address.
            if (buffer.Count > 0 && record.Address != nextAddress)
            {
                regions.Add(BuildRegion(regionStart, buffer));
                buffer.Clear();
            }

            if (buffer.Count == 0)
            {
                regionStart = record.Address;
            }

            for (var i = 0; i < dataLen; i++)
            {
                buffer.Add(record.Data[4 + i]);
            }

            nextAddress = record.Address + (uint)dataLen;
        }

        if (buffer.Count > 0)
        {
            regions.Add(BuildRegion(regionStart, buffer));
        }

        return regions;
    }

    private static FlashCrcRegion BuildRegion(uint physicalStart, List<byte> data)
    {
        var bytes = data.ToArray();
        var crc = new Crc16(bytes).Crc;
        return new FlashCrcRegion(physicalStart | Kseg0Mask, (uint)bytes.Length, crc);
    }
}
