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
    public const uint DEFAULT_BEGIN_PROTECTED_ADDRESS = 0x1D1E0000;

    /// <summary>
    /// Default protected memory range end address (calibration data).
    /// </summary>
    public const uint DEFAULT_END_PROTECTED_ADDRESS = 0x1D200000;

    private readonly uint _beginProtectedAddress;
    private readonly uint _endProtectedAddress;

    /// <summary>
    /// Creates a new Intel HEX parser with the default protected memory range.
    /// </summary>
    public IntelHexParser()
        : this(DEFAULT_BEGIN_PROTECTED_ADDRESS, DEFAULT_END_PROTECTED_ADDRESS)
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
        ArgumentNullException.ThrowIfNull(lines);

        ushort baseAddress = 0;
        var hexRecords = new List<byte[]>();

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
        ArgumentNullException.ThrowIfNull(lines);

        ushort baseAddress = 0;
        var records = new List<HexFileRecord>();

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
            var recordType = hexLine[3];

            baseAddress = UpdateBaseAddress(hexLine, baseAddress, line);

            if (IsProtectedHexRecord(hexLine, baseAddress))
            {
                continue;
            }

            var offsetAddressArray = hexLine.Skip(1).Take(2).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(offsetAddressArray);
            var offsetAddress = BitConverter.ToUInt16(offsetAddressArray, 0);
            var fullAddress = ((uint)baseAddress << 16) | offsetAddress;

            records.Add(new HexFileRecord(fullAddress, hexLine, recordType));
        }

        return records;
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
        // the documented InvalidDataException prevents the downstream BitConverter.ToUInt16 calls (in
        // UpdateBaseAddress / IsProtectedHexRecord / ParseRecords) from reading a wrong-sized slice and
        // throwing a raw ArgumentException — e.g. a type-04 record with a zero byte count would otherwise
        // hand ToUInt16 a zero-length array and crash the whole parse.
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
        if (recordType == 0x04)
        {
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

            var dataArray = hexRecord.Skip(4).Take(2).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(dataArray);
            return BitConverter.ToUInt16(dataArray, 0);
        }
        return currentBaseAddress;
    }

    private bool IsProtectedHexRecord(byte[] hexRecord, ushort baseAddress)
    {
        var recordType = hexRecord[3];

        if (recordType == 0x00)
        {
            var offsetAddressArray = hexRecord.Skip(1).Take(2).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(offsetAddressArray);
            var offsetAddress = BitConverter.ToUInt16(offsetAddressArray, 0);
            var hexRecordAddress = ((uint)baseAddress << 16) | offsetAddress;

            if (hexRecordAddress >= _beginProtectedAddress && hexRecordAddress <= _endProtectedAddress)
            {
                return true;
            }
        }

        return false;
    }
}
