using System.IO;

namespace Daqifi.Core.Firmware;

/// <summary>
/// The size budget a PROGRAM_FLASH frame has to live inside, and the re-packing that keeps every
/// HEX record within it.
/// </summary>
/// <remarks>
/// <para>
/// A PROGRAM_FLASH frame is <c>SOH</c>, then the DLE-escaped opcode, HEX record and CRC-16, then
/// <c>EOT</c>. The two framing bytes are never escaped; every byte between them that collides with
/// <c>SOH</c> (0x01), <c>EOT</c> (0x04) or <c>DLE</c> (0x10) becomes two bytes. The whole frame
/// has to fit one HID OUT report, and the transports refuse a longer one
/// (<c>HidLibraryPlatform.FormatOutputReport</c> and <c>MacOsHidPlatform.Write</c> both throw).
/// </para>
/// <para>
/// That refusal used to arrive during programming, after the erase. The board was then left with
/// its application flash erased and the new image incomplete, and only the firmware repository's
/// button-hold sequence could recover it. Worse, whether it happened at all depended on the image
/// bytes: a 32-byte record of ordinary data frames small enough, and the same record full of
/// 0x01/0x04/0x10 bytes does not. This type makes the question data-independent — a record is
/// judged by its <em>worst-case</em> expansion, so the answer is the same for every image of the
/// same shape — and it is asked before any device I/O (see
/// <c>Pic32BootloaderSession.PrepareHexImage</c>).
/// </para>
/// </remarks>
internal static class Pic32BootloaderFrameBudget
{
    /// <summary>
    /// The bootloader's fixed HID OUT report length, in bytes.
    /// </summary>
    internal const int HidOutputReportLength = 64;

    /// <summary>
    /// The longest frame one HID OUT report can carry. The HidSharp transport prepends a report-ID
    /// byte to the frame, so it is one byte less than the report itself. The macOS IOKit transport
    /// passes the report ID separately and so has one byte more to spare; the library budgets for
    /// the tighter of the two, because the same image must flash on both.
    /// </summary>
    internal const int MaxFrameLength = HidOutputReportLength - 1;

    /// <summary>SOH and EOT. Both sit outside the escaped body, so neither can expand.</summary>
    private const int FramingByteCount = 2;

    /// <summary>The opcode and the two CRC-16 bytes that share the escaped body with the record.</summary>
    private const int OpcodeAndCrcByteCount = 3;

    /// <summary>Intel HEX record bytes that are not data: byte count, 2 address bytes, record type, checksum.</summary>
    private const int IntelHexRecordOverhead = 5;

    /// <summary>Intel HEX record type for a data record.</summary>
    private const byte IntelHexDataRecordType = 0x00;

    /// <summary>One more than the largest 16-bit record offset, used to catch a record that wraps.</summary>
    private const int AddressBlockLength = 0x10000;

    /// <summary>
    /// The longest HEX record that always fits one report. Every byte of the escaped body can
    /// double, so the body may be twice the opcode, record and CRC together:
    /// <c>2 + 2 * (recordLength + 3) &lt;= 63</c> gives 27 bytes.
    /// </summary>
    internal const int MaxSafeRecordLength = ((MaxFrameLength - FramingByteCount) / 2) - OpcodeAndCrcByteCount;

    /// <summary>
    /// The data bytes a record of <see cref="MaxSafeRecordLength"/> can carry: 22.
    /// </summary>
    internal const int MaxSafeDataByteCount = MaxSafeRecordLength - IntelHexRecordOverhead;

    /// <summary>
    /// The data bytes each record carries after a split. 16 is what XC32 itself emits, and it is
    /// below <see cref="MaxSafeDataByteCount"/>, so a split record always fits one report.
    /// </summary>
    internal const int SplitDataByteCount = 16;

    /// <summary>
    /// The longest frame a record of <paramref name="recordLength"/> bytes can produce — the
    /// length when every byte of the escaped body needs an escape.
    /// </summary>
    internal static int WorstCaseFrameLength(int recordLength)
        => FramingByteCount + (2 * (recordLength + OpcodeAndCrcByteCount));

    /// <summary>
    /// Re-packs <paramref name="records"/> so every record's worst-case frame fits one HID OUT
    /// report. A record already within <see cref="MaxSafeRecordLength"/> is passed through
    /// untouched. A longer data record is split into records of at most
    /// <see cref="SplitDataByteCount"/> data bytes, each with its own offset address and Intel HEX
    /// checksum. The bytes programmed, and the addresses they are programmed at, do not change.
    /// </summary>
    /// <param name="records">The records the HEX image parsed to.</param>
    /// <returns>The records to program, in the same order.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when a record is too long and the library cannot split it. The caller runs this
    /// before any device I/O, so the board is still intact when this throws.
    /// </exception>
    internal static List<byte[]> FitRecordsToOneReport(IReadOnlyList<byte[]> records)
    {
        var fitted = new List<byte[]>(records.Count);

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Length <= MaxSafeRecordLength)
            {
                fitted.Add(record);
                continue;
            }

            fitted.AddRange(SplitDataRecord(record, index));
        }

        return fitted;
    }

    /// <summary>
    /// Confirms every record's worst-case frame fits one HID OUT report. This is the last word
    /// before the erase: <see cref="FitRecordsToOneReport"/> is meant to leave nothing for it to
    /// find, so a record reaching it is a record the library must refuse rather than program.
    /// </summary>
    /// <param name="records">The records about to be programmed.</param>
    /// <exception cref="InvalidDataException">Thrown when any record is too long.</exception>
    internal static void EnsureEveryRecordFitsOneReport(IReadOnlyList<byte[]> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Length > MaxSafeRecordLength)
            {
                throw TooLong(records[index], index, "the library cannot make it any smaller");
            }
        }
    }

    private static IEnumerable<byte[]> SplitDataRecord(byte[] record, int index)
    {
        // Only a well-formed Intel HEX data record can be re-packed: the split rewrites the byte
        // count, the offset address and the checksum, so all three have to mean what they say.
        // Anything else is refused rather than reshaped on a guess.
        if (record.Length < IntelHexRecordOverhead
            || record[3] != IntelHexDataRecordType
            || record[0] != record.Length - IntelHexRecordOverhead)
        {
            throw TooLong(record, index, "it is not a well-formed Intel HEX data record, so the library cannot split it");
        }

        int dataLength = record[0];
        var offset = (record[1] << 8) | record[2];
        if (offset + dataLength > AddressBlockLength)
        {
            throw TooLong(record, index, "its bytes run past the end of its 64 KB address block, so the library cannot split it");
        }

        return SplitChunks(record, dataLength, offset);
    }

    private static IEnumerable<byte[]> SplitChunks(byte[] record, int dataLength, int offset)
    {
        for (var start = 0; start < dataLength; start += SplitDataByteCount)
        {
            var chunkLength = Math.Min(SplitDataByteCount, dataLength - start);
            var chunkOffset = offset + start;

            var chunk = new byte[IntelHexRecordOverhead + chunkLength];
            chunk[0] = (byte)chunkLength;
            chunk[1] = (byte)(chunkOffset >> 8);
            chunk[2] = (byte)chunkOffset;
            chunk[3] = IntelHexDataRecordType;
            Array.Copy(record, 4 + start, chunk, 4, chunkLength);

            // Intel HEX checksum: the two's complement of the sum of every earlier byte.
            byte sum = 0;
            for (var i = 0; i < chunk.Length - 1; i++)
            {
                sum += chunk[i];
            }

            chunk[^1] = (byte)-sum;

            yield return chunk;
        }
    }

    private static InvalidDataException TooLong(byte[] record, int index, string reason)
    {
        return new InvalidDataException(
            $"Firmware HEX record {index + 1} is {record.Length} bytes long. Escaped, it can make a " +
            $"bootloader frame of {WorstCaseFrameLength(record.Length)} bytes, and one HID report carries " +
            $"{MaxFrameLength} bytes. The image is refused and the device flash is untouched, because " +
            $"{reason}.");
    }
}
