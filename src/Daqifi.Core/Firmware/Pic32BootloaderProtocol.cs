namespace Daqifi.Core.Firmware;

/// <summary>
/// Implements the PIC32 bootloader protocol, delegating to the static
/// message producer/consumer and HEX parser classes.
/// All operations are transport-agnostic.
/// </summary>
internal class Pic32BootloaderProtocol : IBootloaderProtocol
{
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
        // HexFileRecord.Data is the raw record the parser decoded, which is exactly what
        // ParseHexRecords returns, so this projection changes nothing about the records
        // themselves — it only lets the checks in ParseWritableRecords see their addresses.
        return ParseWritableRecords(hexFileLines).ConvertAll(record => record.Data);
    }

    /// <inheritdoc />
    public IReadOnlyList<FlashCrcRegion> ComputeCrcRegions(string[] hexFileLines)
    {
        // Order data records by physical address so adjacent records coalesce
        // regardless of file order. The device computes its flash CRC over the
        // address-ordered bytes in a region, so this matches what READ_CRC will
        // return. Protected-range records are already excluded, exactly as they
        // are excluded from programming. Data the bootloader would place elsewhere
        // or out of the window is refused, not quietly left out of the regions:
        // dropping it here would hand back a CRC over a subset of the image and
        // let the flow erase the board and program an incomplete one.
        var dataRecords = ParseWritableRecords(hexFileLines)
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

    /// <summary>
    /// Parses the image and refuses one whose records the bootloader would not program where this
    /// library checks and verifies them. See <see cref="Pic32ApplicationFlash"/>.
    /// </summary>
    private List<HexFileRecord> ParseWritableRecords(string[] hexFileLines)
    {
        // Protected-range records are dropped by the parser first, then what is left has to lie
        // inside the flash the bootloader erases and programs. That order is the point: the
        // default protected range sits above the window, so an image carrying records there keeps
        // having them dropped, as this library already shipped, instead of being refused. A record
        // the bootloader never acknowledges is refused first. The placement check runs before the
        // window check because the window check reads the host's addresses, which are only the
        // device's addresses once the placement check has passed.
        var records = _hexParser.ParseRecords(hexFileLines);
        Pic32ApplicationFlash.EnsureTheBootloaderAcceptsEveryRecordType(records);
        Pic32ApplicationFlash.EnsureTheBootloaderPlacesEveryRecordWhereTheHostDoes(records);
        Pic32ApplicationFlash.EnsureEveryRecordIsInsideTheWritableWindow(records);
        return records;
    }

    private static FlashCrcRegion BuildRegion(uint physicalStart, List<byte> data)
    {
        var bytes = data.ToArray();
        var crc = new Crc16(bytes).Crc;
        return new FlashCrcRegion(physicalStart | Pic32ApplicationFlash.Kseg0Mask, (uint)bytes.Length, crc);
    }
}
