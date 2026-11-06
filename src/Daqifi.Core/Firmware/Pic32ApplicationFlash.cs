using System.IO;

namespace Daqifi.Core.Firmware;

/// <summary>
/// The window of PIC32 program flash a firmware update may place bytes in, and the check that
/// refuses an image reaching outside it.
/// </summary>
/// <remarks>
/// <para>
/// The bootloader erases and programs only the <em>lower</em> program-flash panel. Its
/// <c>APP_FlashErase</c> runs <c>LOWER_FLASH_REGION_ERASE_OPERATION</c>
/// (<c>bootloader/firmware/src/system_config/usbdevice_pic32mz_ef_sk/framework/bootloader/src/nvm.c</c>,
/// line 195 on the firmware's <c>main</c>), which clears physical
/// <c>0x1D000000</c>–<c>0x1D0FFFFF</c> only, so that the NVM settings pages at <c>0x1D1E0000</c>
/// survive a customer update (firmware #909). <c>system_config.h</c> narrows the write bound to
/// match: <c>APP_FLASH_BASE_ADDRESS</c> is <c>0x9D000000</c> (line 240),
/// <c>APP_FLASH_LOWER_PANEL_SIZE</c> is <c>0x100000</c> (line 274) and
/// <c>APP_FLASH_END_ADDRESS</c> is <c>APP_FLASH_BASE_ADDRESS + APP_FLASH_LOWER_PANEL_SIZE - 1</c>
/// (line 276), i.e. KSEG0 <c>0x9D0FFFFF</c> — physical <c>0x1D0FFFFF</c>. The constants below are
/// those three, in their physical form.
/// </para>
/// <para>
/// What the device does with a record outside that window is not a success either way.
/// <c>APP_ProgramHexRecord</c> drops a boot-flash or configuration-word address silently and still
/// ACKs (firmware #764), and fails the record outright for an address in the unerased upper panel
/// (firmware #1141, <c>nvm.c</c> line 381). Both leave an incomplete image on a board whose
/// application flash has already been erased, and a half-flashed PIC32 boots garbage on the next
/// power cycle — a power-on reset does not arm the bootloader's button window, so recovery needs
/// the firmware repository's physical button-hold sequence.
/// </para>
/// <para>
/// The firmware's own release script refuses such an image before it can ship
/// (<c>tools/release/cut_release.sh</c> fails the release when any data record's span reaches
/// <c>0x1D100000</c> or <c>0x1FC00000</c>), but a client library is the only guard for a hand-built
/// or third-party HEX a user picks themselves. So this check mirrors that gate, and it runs before
/// any device command goes out (see <c>Pic32BootloaderSession.PrepareHexImage</c>) — while the
/// board still holds its application.
/// </para>
/// </remarks>
internal static class Pic32ApplicationFlash
{
    /// <summary>
    /// PIC32 KSEG0 mask (<c>PA_TO_KVA0</c>): the bootloader programs records at
    /// <c>PA_TO_KVA0(physicalAddress)</c> and READ_CRC reads at <c>KVA0_TO_KVA1(address)</c>, so the
    /// address handed to READ_CRC must be the KSEG0 form of the HEX record's physical address.
    /// </summary>
    internal const uint Kseg0Mask = 0x80000000;

    /// <summary>
    /// First physical address the bootloader erases and programs. The firmware's
    /// <c>APP_FLASH_BASE_ADDRESS</c> (<c>0x9D000000</c>) without its KSEG0 mask.
    /// </summary>
    internal const uint PhysicalStart = 0x1D000000;

    /// <summary>
    /// Size of the lower program-flash panel, the firmware's <c>APP_FLASH_LOWER_PANEL_SIZE</c>.
    /// </summary>
    internal const uint LowerPanelSize = 0x100000;

    /// <summary>
    /// Last physical address the bootloader erases and programs: the firmware's
    /// <c>APP_FLASH_END_ADDRESS</c> without its KSEG0 mask. The upper panel from
    /// <c>0x1D100000</c> up is deliberately left alone, so nothing may be placed there.
    /// </summary>
    internal const uint PhysicalEnd = PhysicalStart + LowerPanelSize - 1;

    /// <summary>KSEG0 form of <see cref="PhysicalStart"/>, the firmware's <c>APP_FLASH_BASE_ADDRESS</c>.</summary>
    internal const uint Kseg0Start = PhysicalStart | Kseg0Mask;

    /// <summary>KSEG0 form of <see cref="PhysicalEnd"/>, the firmware's <c>APP_FLASH_END_ADDRESS</c>.</summary>
    internal const uint Kseg0End = PhysicalEnd | Kseg0Mask;

    /// <summary>Intel HEX record type for a data record.</summary>
    private const byte IntelHexDataRecordType = 0x00;

    /// <summary>Intel HEX record bytes that are not data: byte count, 2 address bytes, record type, checksum.</summary>
    private const int IntelHexRecordOverhead = 5;

    /// <summary>
    /// Tells whether every byte of a <paramref name="length"/>-byte span starting at
    /// <paramref name="physicalStart"/> lies inside the writable window.
    /// </summary>
    /// <remarks>
    /// The whole span has to be in range, not just its start. The bootloader decides per word
    /// (<c>APP_ProgramHexRecord</c> tests each <c>ProgAddress</c> on its own), so a record
    /// straddling the top of the panel would have its tail dropped or refused while the host
    /// counted the record as programmed. The firmware's release gate reads a record the same way,
    /// as the half-open span <c>[start, start + count)</c>.
    /// </remarks>
    internal static bool ContainsWholeSpan(uint physicalStart, uint length)
    {
        if (physicalStart < PhysicalStart || physicalStart > PhysicalEnd)
        {
            return false;
        }

        // The span's last address is start + length - 1, which can pass 0xFFFFFFFF for a record
        // near the top of the address space. Ask instead whether the byte count fits in the bytes
        // remaining to the end of the window, so that address is never formed.
        return length == 0 || length - 1 <= PhysicalEnd - physicalStart;
    }

    /// <summary>
    /// Refuses an image that places any byte outside the writable window.
    /// </summary>
    /// <param name="records">
    /// The records the HEX image parsed to, <em>after</em> protected-range records have been
    /// dropped. That order matters: a normal image carries calibration records at
    /// <c>0x1D1E0000</c>, which the parser drops and the bootloader deliberately does not erase, so
    /// checking the window first would refuse an image that is perfectly fine.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Thrown when a data record puts a byte outside <see cref="PhysicalStart"/>..<see cref="PhysicalEnd"/>,
    /// naming the offending record's address. The caller runs this before any device I/O, so the
    /// board still holds its application when this throws.
    /// </exception>
    internal static void EnsureEveryRecordIsInsideTheWritableWindow(IReadOnlyList<HexFileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            if (record.RecordType != IntelHexDataRecordType || record.Data.Length < IntelHexRecordOverhead)
            {
                continue;
            }

            // IntelHexParser has already checked that the byte-count field agrees with the data
            // actually present, so the count is the real length of the record's span.
            uint length = record.Data[0];

            // A data record with a byte count of zero places no byte anywhere, so there is no
            // address to judge it on. Refusing a whole image over a record that programs nothing
            // would be refusing on no evidence; an image that is nothing but such records is
            // caught by Pic32BootloaderImageContent instead, which is the guard for an image with
            // nothing to program (#838).
            if (length == 0 || ContainsWholeSpan(record.Address, length))
            {
                continue;
            }

            var lastAddress = (ulong)record.Address + length - 1;

            throw new InvalidDataException(
                $"Firmware HEX file places data outside the flash the bootloader programs. A data " +
                $"record covers 0x{record.Address:X8}-0x{lastAddress:X8} ({length} byte(s)), and the " +
                $"bootloader erases and programs only 0x{PhysicalStart:X8}-0x{PhysicalEnd:X8}, the " +
                $"lower program-flash panel. Those bytes would be dropped or refused by the device, " +
                $"leaving an incomplete image on a board whose application flash had already been " +
                $"erased. The image is refused and the device flash is untouched.");
        }
    }
}
