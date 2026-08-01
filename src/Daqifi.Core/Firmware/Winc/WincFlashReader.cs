namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// Read-only access to the WINC1500's SPI flash and identity registers, driven through the serial
/// bridge. Every operation here is non-destructive: identity reads and flash reads only.
/// </summary>
/// <remarks>
/// <para>
/// The WINC's flash controller is a set of memory-mapped registers, so a flash read is a scripted
/// register sequence followed by a block read of the shared memory window — the same sequence the
/// WINC host driver's <c>spi_flash.c</c> uses. Notably this needs no <c>programmer_firmware.bin</c>
/// blob; that upload is only required for the erase/program path.
/// </para>
/// <para>
/// Erase and program are deliberately absent. They are the operations that can brick a module, and
/// nothing in this class can leave the WINC in a worse state than it started.
/// </para>
/// </remarks>
internal sealed class WincFlashReader
{
    /// <summary>Chip identity register. A live WINC1500 reads 0x0015xxxx, or 0x0010xxxx once halted in download mode.</summary>
    internal const uint ChipIdRegister = 0x1000;

    private const uint SpiFlashBase = 0x10200;
    private const uint RegCommandCount = SpiFlashBase + 0x04;
    private const uint RegDataCount = SpiFlashBase + 0x08;
    private const uint RegBuffer1 = SpiFlashBase + 0x0C;
    private const uint RegBuffer2 = SpiFlashBase + 0x10;
    private const uint RegBufferDirection = SpiFlashBase + 0x14;
    private const uint RegTransferDone = SpiFlashBase + 0x18;
    private const uint RegDmaAddress = SpiFlashBase + 0x1C;

    /// <summary>Scratch window in WINC memory that flash reads are staged through.</summary>
    private const uint HostShareMemoryBase = 0xD0000;

    /// <summary>Register the flash controller parks a byte-wide result in.</summary>
    private const uint DummyRegister = 0x1084;

    // SPI NOR flash commands (MX25L-compatible).
    private const byte FlashCommandFastRead = 0x0B;
    private const byte FlashCommandReadIdentification = 0x9F;

    /// <summary>Dummy byte the fast-read command clocks out before data.</summary>
    private const byte FastReadDummyByte = 0xA5;

    /// <summary>Bit that starts a flash-controller transfer.</summary>
    private const uint CommandStartBit = 1u << 7;

    /// <summary>
    /// Highest addressable flash byte. The fast-read command carries a 24-bit address, so this is
    /// what the protocol can express regardless of the part's actual capacity.
    /// </summary>
    internal const uint MaxFlashAddress = 0xFFFFFF;

    private readonly WincSerialBridgeClient _bridge;
    private readonly int _transferPollLimit;

    internal WincFlashReader(WincSerialBridgeClient bridge, int transferPollLimit = 1000)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _transferPollLimit = transferPollLimit;
    }

    /// <summary>
    /// Reads the WINC chip identity register.
    /// </summary>
    internal uint ReadChipId() => _bridge.ReadRegister(ChipIdRegister);

    /// <summary>
    /// True when a chip ID looks like a reachable WINC1500 — family 0x15 (firmware running) or
    /// 0x10 (halted in download mode). Anything else means the bridge is up but the WINC behind it
    /// is not answering, which is worth distinguishing from a dead port.
    /// </summary>
    internal static bool IsKnownWincChipId(uint chipId)
    {
        var family = chipId >> 16;
        return family is 0x15 or 0x10;
    }

    /// <summary>
    /// Reads the SPI flash JEDEC identification word (command 0x9F).
    /// </summary>
    internal uint ReadFlashJedecId()
    {
        _bridge.WriteRegister(RegDataCount, 0);
        _bridge.WriteRegister(RegBuffer1, FlashCommandReadIdentification);
        _bridge.WriteRegister(RegBufferDirection, 0x01);
        _bridge.WriteRegister(RegDmaAddress, 0);
        _bridge.WriteRegister(RegCommandCount, 1 | CommandStartBit);

        WaitForTransferDone("read flash JEDEC id");

        return _bridge.ReadRegister(DummyRegister);
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes of SPI flash starting at <paramref name="offset"/>,
    /// chunking to stay inside both the flash controller's staging window and the bridge's
    /// block-read ceiling.
    /// </summary>
    internal byte[] ReadFlash(uint offset, int length, CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Read length must be positive.");
        }

        // Bounds-check in 64-bit before any address arithmetic. Without this the per-chunk
        // `offset + read` would silently wrap past 32 bits and quietly read the wrong part of
        // flash — returning plausible-looking data with no error at all.
        var endExclusive = (long)offset + length;
        if (endExclusive > (long)MaxFlashAddress + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A read of {length} bytes at 0x{offset:X6} would end at 0x{endExclusive:X}, past the " +
                $"highest addressable flash byte 0x{MaxFlashAddress:X6}.");
        }

        var result = new byte[length];
        var read = 0;

        while (read < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = Math.Min(WincBridgeProtocol.MaxReadBlockSize, length - read);
            // Safe: the bounds check above guarantees offset + read stays within 24 bits.
            var chunkData = ReadFlashChunk((uint)(offset + read), chunk);
            Buffer.BlockCopy(chunkData, 0, result, read, chunk);
            read += chunk;
        }

        return result;
    }

    /// <summary>
    /// Stages one chunk of flash into the shared memory window and reads it back. Mirrors the host
    /// driver's <c>spi_flash_load_to_cortus_mem</c> followed by a block read.
    /// </summary>
    private byte[] ReadFlashChunk(uint flashAddress, int size)
    {
        // The fast-read command word packs the opcode and the 24-bit address into one register,
        // opcode in the low byte and address bytes ascending from there.
        var commandWord = (uint)FlashCommandFastRead
                        | ((flashAddress >> 16) & 0xFFu) << 8
                        | ((flashAddress >> 8) & 0xFFu) << 16
                        | (flashAddress & 0xFFu) << 24;

        _bridge.WriteRegister(RegDataCount, (uint)size);
        _bridge.WriteRegister(RegBuffer1, commandWord);
        _bridge.WriteRegister(RegBuffer2, FastReadDummyByte);
        _bridge.WriteRegister(RegBufferDirection, 0x1F);
        _bridge.WriteRegister(RegDmaAddress, HostShareMemoryBase);
        _bridge.WriteRegister(RegCommandCount, 5 | CommandStartBit);

        WaitForTransferDone($"read {size} flash bytes at 0x{flashAddress:X6}");

        return _bridge.ReadBlock(HostShareMemoryBase, size);
    }

    /// <summary>
    /// Polls the flash controller's done flag. Bounded rather than a bare spin: the firmware's own
    /// equivalent loop is unbounded, and a WINC that stops answering would otherwise hang the host.
    /// </summary>
    private void WaitForTransferDone(string operation)
    {
        for (var attempt = 0; attempt < _transferPollLimit; attempt++)
        {
            if (_bridge.ReadRegister(RegTransferDone) == 1)
            {
                return;
            }
        }

        throw new TimeoutException(
            $"WINC flash controller never reported transfer-done while attempting to {operation} " +
            $"(polled {_transferPollLimit} times).");
    }
}
