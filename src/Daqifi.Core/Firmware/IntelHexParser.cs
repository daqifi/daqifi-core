using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Parses Intel HEX format files into structured records, with support for
/// memory protection ranges to skip calibration data regions.
/// </summary>
public class IntelHexParser
{
    /// <summary>
    /// Default protected memory range start address (calibration data).
    /// </summary>
    public const uint DefaultBeginProtectedAddress = 0x1D1E0000;

    /// <summary>
    /// Default protected memory range end address (calibration data).
    /// </summary>
    public const uint DefaultEndProtectedAddress = 0x1D200000;

    /// <summary>
    /// Default protected memory range start address (calibration data).
    /// </summary>
    /// <remarks>
    /// Kept so that source written against the shipped name still compiles, which is the
    /// compatibility this library promises (see <c>docs/adr/0002-binary-compatibility-policy.md</c>).
    /// Because it is a <see langword="const" />, an assembly compiled against the old name has
    /// the literal value baked in and does not reference this field at all - only source that is
    /// recompiled sees the obsoletion warning.
    /// </remarks>
    [Obsolete($"Use {nameof(DefaultBeginProtectedAddress)} instead. This name will be removed in a future major version.")]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
        Justification = "Deliberately preserves the shipped spelling; the replacement above is the conforming name.")]
    public const uint DEFAULT_BEGIN_PROTECTED_ADDRESS = DefaultBeginProtectedAddress;

    /// <summary>
    /// Default protected memory range end address (calibration data).
    /// </summary>
    /// <remarks>
    /// Kept for source compatibility; see <see cref="DEFAULT_BEGIN_PROTECTED_ADDRESS" />.
    /// </remarks>
    [Obsolete($"Use {nameof(DefaultEndProtectedAddress)} instead. This name will be removed in a future major version.")]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
        Justification = "Deliberately preserves the shipped spelling; the replacement above is the conforming name.")]
    public const uint DEFAULT_END_PROTECTED_ADDRESS = DefaultEndProtectedAddress;

    private readonly uint _beginProtectedAddress;
    private readonly uint _endProtectedAddress;

    /// <summary>
    /// Creates a new Intel HEX parser with the default protected memory range.
    /// </summary>
    public IntelHexParser()
        : this(DefaultBeginProtectedAddress, DefaultEndProtectedAddress)
    {
    }

    /// <summary>
    /// Creates a new Intel HEX parser with a custom protected memory range.
    /// </summary>
    /// <param name="beginProtectedAddress">Start of the protected memory range.</param>
    /// <param name="endProtectedAddress">End of the protected memory range.</param>
    public IntelHexParser(uint beginProtectedAddress, uint endProtectedAddress)
    {
        _beginProtectedAddress = beginProtectedAddress;
        _endProtectedAddress = endProtectedAddress;
    }

    /// <summary>
    /// Parses Intel HEX formatted lines into a list of raw hex record byte arrays,
    /// filtering out records in the protected memory range.
    /// </summary>
    /// <param name="lines">The lines from the HEX file (each starting with ':').</param>
    /// <returns>A list of byte arrays, each representing one hex record.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lines"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown when a line is malformed or has an invalid checksum.</exception>
    public List<byte[]> ParseHexRecords(string[] lines)
    {
        // Validate eagerly, before touching the iterator, so a null array throws here at the
        // call rather than on first enumeration.
        ArgumentNullException.ThrowIfNull(lines);

        var hexRecords = new List<byte[]>();

        foreach (var (hexLine, _) in EnumerateUnprotectedRecords(lines))
        {
            hexRecords.Add(hexLine);
        }

        return hexRecords;
    }

    /// <summary>
    /// Parses Intel HEX formatted lines into structured records with full addresses.
    /// Records in the protected memory range are filtered out.
    /// </summary>
    /// <param name="lines">The lines from the HEX file (each starting with ':').</param>
    /// <returns>A list of <see cref="HexFileRecord"/> with computed addresses.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lines"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown when a line is malformed or has an invalid checksum.</exception>
    public List<HexFileRecord> ParseRecords(string[] lines)
    {
        // Validate eagerly, before touching the iterator, so a null array throws here at the
        // call rather than on first enumeration.
        ArgumentNullException.ThrowIfNull(lines);

        var records = new List<HexFileRecord>();

        foreach (var (hexLine, baseAddress) in EnumerateUnprotectedRecords(lines))
        {
            records.Add(new HexFileRecord(ComputeFullAddress(hexLine, baseAddress), hexLine, hexLine[3]));
        }

        return records;
    }

    /// <summary>
    /// Walks the HEX lines once: skips blank lines, validates and decodes each record, tracks
    /// the running extended-linear base address, and yields only the records that fall outside
    /// the protected memory range. This is the sequencing shared by both public parse methods;
    /// they differ only in what they project from each yielded record.
    /// </summary>
    /// <remarks>
    /// This is a lazy iterator and deliberately performs no argument validation of its own.
    /// A <c>ThrowIfNull</c> placed here would not run until first enumeration, which would move
    /// where the public methods' documented <see cref="ArgumentNullException"/> surfaces; each
    /// public method therefore keeps its own eager guard. The <see cref="InvalidDataException"/>s
    /// raised while walking are unaffected: both callers enumerate to completion inside their own
    /// body, so those still surface from the public call exactly as before.
    /// </remarks>
    private IEnumerable<(byte[] HexLine, ushort BaseAddress)> EnumerateUnprotectedRecords(string[] lines)
    {
        ushort baseAddress = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ValidateLine(line);

            var hexLine = ConvertLineToBytes(line);
            ValidateRecordLength(hexLine, line);
            ValidateChecksum(hexLine, line);

            baseAddress = UpdateBaseAddress(hexLine, baseAddress, line);

            if (IsProtectedHexRecord(hexLine, baseAddress))
            {
                continue;
            }

            yield return (hexLine, baseAddress);
        }
    }

    private static void ValidateLine(string line)
    {
        if (line[0] != ':')
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" doesn't start with the colon character \":\"");
        }

        if (line.Length % 2 != 1)
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" doesn't contain an odd number of characters");
        }

        if (line.Length < 11)
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" is too short to be a valid record");
        }
    }

    private static void ValidateRecordLength(byte[] record, string line)
    {
        // Intel HEX structure: 1 (byte count) + 2 (address) + 1 (record type) + N (data) + 1 (checksum),
        // where N is the value of the byte-count field (record[0]). ValidateLine already guarantees at
        // least 5 decoded bytes, so record[0] is safely accessible. A declared count that disagrees with
        // the actual data length means the record is truncated, padded, or corrupt. Rejecting it here as
        // the documented InvalidDataException, rather than being parsed as if it were intact.
        //
        // This check is NOT what keeps the ReadBigEndianUInt16 reads in range. ComputeFullAddress reads
        // bytes 1-2, which ValidateLine's 11-character minimum already guarantees; and a zero-count
        // type-04 record passes right through here, because its declared count does equal its (zero)
        // data length — that case is caught by UpdateBaseAddress's own explicit type-04 guard below.
        int declaredDataLength = record[0];
        int actualDataLength = record.Length - 5;
        if (actualDataLength != declaredDataLength)
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" declares {declaredDataLength} data byte(s) but contains {actualDataLength}");
        }
    }

    private static void ValidateChecksum(byte[] record, string line)
    {
        byte sum = 0;
        foreach (var b in record)
        {
            sum += b;
        }

        if (sum != 0)
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" has an invalid checksum");
        }
    }

    private static byte[] ConvertLineToBytes(string line)
    {
        var hexLine = new byte[(line.Length - 1) / 2];

        for (var i = 1; i < line.Length; i += 2)
        {
            var hex = line.Substring(i, 2);
            try
            {
                hexLine[(i - 1) / 2] = byte.Parse(hex, NumberStyles.HexNumber);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"The hex record \"{line}\" contains invalid hex characters \"{hex}\"", ex);
            }
        }

        return hexLine;
    }

    private static ushort UpdateBaseAddress(byte[] hexRecord, ushort currentBaseAddress, string line)
    {
        var recordType = hexRecord[3];
        if (recordType != 0x04)
        {
            // Only a type-04 (extended linear address) record changes the base address; every
            // other record type leaves it as it stands.
            return currentBaseAddress;
        }

        // A type-04 (extended linear address) record must carry exactly 2 data bytes — the
        // upper 16 bits of the 32-bit address. Its byte-count field can equal its data length
        // yet still be wrong (e.g. a zero-count type-04 record whose count "matches" its zero
        // data bytes), so ValidateRecordLength alone doesn't cover this. Guard the slice
        // explicitly so a short/corrupt type-04 record throws the documented InvalidDataException
        // rather than a raw ArgumentException from BitConverter.ToUInt16 on an undersized array.
        if (hexRecord.Length - 5 != 2)
        {
            throw new InvalidDataException(
                $"The hex record \"{line}\" is a type-04 extended-address record but does not carry exactly 2 data bytes");
        }

        return ReadBigEndianUInt16(hexRecord, 4);
    }

    /// <summary>
    /// Computes the record's full 32-bit address: the running extended-linear base address in
    /// the high 16 bits, and the record's own offset address - bytes 1-2 of every Intel HEX
    /// record - in the low 16 bits.
    /// </summary>
    /// <remarks>
    /// Both the protected-range test and the address reported on a parsed
    /// <see cref="HexFileRecord"/> are this same value, so they must be computed the same way:
    /// if they disagreed, a record could be filtered on one address and reported under another.
    /// </remarks>
    private static uint ComputeFullAddress(byte[] hexRecord, ushort baseAddress)
    {
        return ((uint)baseAddress << 16) | ReadBigEndianUInt16(hexRecord, 1);
    }

    /// <summary>
    /// Reads the two bytes at <paramref name="offset"/> as a big-endian <see cref="ushort"/>,
    /// which is the byte order Intel HEX uses for both the offset address and the type-04
    /// extended-address payload.
    /// </summary>
    private static ushort ReadBigEndianUInt16(byte[] record, int offset)
    {
        var bytes = record.Skip(offset).Take(2).ToArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    private bool IsProtectedHexRecord(byte[] hexRecord, ushort baseAddress)
    {
        var recordType = hexRecord[3];
        if (recordType != 0x00)
        {
            return false;
        }

        var hexRecordAddress = ComputeFullAddress(hexRecord, baseAddress);

        return hexRecordAddress >= _beginProtectedAddress && hexRecordAddress <= _endProtectedAddress;
    }
}
