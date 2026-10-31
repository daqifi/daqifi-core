using System.IO;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Whether a parsed HEX image has any firmware in it, measured in the bytes the bootloader will
/// program rather than in the bytes the records are made of.
/// </summary>
/// <remarks>
/// <para>
/// An Intel HEX record is a byte count, two address bytes, a record type, the data and a checksum.
/// Only a type-00 data record carries bytes to program; a type-01 end-of-file record and a type-04
/// extended-linear-address record carry none. So the length of the records says nothing about
/// whether there is firmware to write: an end-of-file record on its own is 5 bytes long and
/// programs nothing.
/// </para>
/// <para>
/// This is asked before any device I/O (see <c>Pic32BootloaderSession.PrepareHexImage</c>), because
/// the flow erases the application flash first and programs second. An image with nothing to
/// program used to pass, and the board was then left erased with no application on it, which only
/// the firmware repository's button-hold sequence recovers (#838).
/// </para>
/// </remarks>
internal static class Pic32BootloaderImageContent
{
    /// <summary>Intel HEX record type for a data record.</summary>
    private const byte IntelHexDataRecordType = 0x00;

    /// <summary>Intel HEX record bytes that are not data: byte count, 2 address bytes, record type, checksum.</summary>
    private const int IntelHexRecordOverhead = 5;

    /// <summary>
    /// Counts the data bytes <paramref name="records"/> will program. Only a well-formed type-00
    /// data record adds to the total, and it adds its byte count, not its length.
    /// </summary>
    /// <param name="records">The records the HEX image parsed to, after protected-range filtering.</param>
    /// <returns>The number of flash bytes the image programs.</returns>
    internal static long CountProgrammableDataBytes(IReadOnlyList<byte[]> records)
    {
        long dataBytes = 0;

        foreach (var record in records)
        {
            // A record too short to hold a record type cannot come from IntelHexParser, which
            // refuses any line below 11 characters. Count it as programmable rather than guess it
            // away: this count decides whether to refuse an image, so the only safe direction to
            // be wrong in is the one that refuses nothing it cannot show is empty.
            if (record.Length < IntelHexRecordOverhead)
            {
                dataBytes += record.Length;
                continue;
            }

            if (record[3] != IntelHexDataRecordType)
            {
                continue;
            }

            // The byte count has to agree with the data the record carries, or the record means
            // nothing. IntelHexParser rejects a record where the two disagree, so reaching here
            // with one is not a case to interpret.
            if (record[0] == record.Length - IntelHexRecordOverhead)
            {
                dataBytes += record[0];
            }
        }

        return dataBytes;
    }

    /// <summary>
    /// Refuses an image that programs no flash byte at all.
    /// </summary>
    /// <param name="records">The records the HEX image parsed to, after protected-range filtering.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when no record in the image carries a byte to program. The caller runs this before
    /// any device I/O, so the board still holds its application when this throws.
    /// </exception>
    internal static void EnsureImageHasDataToProgram(IReadOnlyList<byte[]> records)
    {
        if (CountProgrammableDataBytes(records) > 0)
        {
            return;
        }

        throw new InvalidDataException(
            $"Firmware HEX file did not contain any writable records. It parsed to {records.Count} " +
            "record(s), and none of them is a data record that carries a byte to program, so there " +
            "is nothing to write to the device. The image is refused and the device flash is " +
            "untouched.");
    }
}
