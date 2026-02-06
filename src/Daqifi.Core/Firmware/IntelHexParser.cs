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
    private ushort _baseAddress;

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
    /// <exception cref="InvalidDataException">Thrown when a line is malformed.</exception>
    public List<byte[]> ParseHexRecords(string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _baseAddress = 0;
        var hexRecords = new List<byte[]>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ValidateLine(line);

            var hexLine = ConvertLineToBytes(line);

            if (IsProtectedHexRecord(hexLine))
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
    /// <exception cref="InvalidDataException">Thrown when a line is malformed.</exception>
    public List<HexFileRecord> ParseRecords(string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _baseAddress = 0;
        var records = new List<HexFileRecord>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ValidateLine(line);

            var hexLine = ConvertLineToBytes(line);
            var recordType = hexLine[3];

            // Update base address for extended linear address records
            if (recordType == 0x04)
            {
                var dataArray = hexLine.Skip(4).Take(hexLine.Length - 5).ToArray();
                if (BitConverter.IsLittleEndian) Array.Reverse(dataArray);
                _baseAddress = BitConverter.ToUInt16(dataArray, 0);
            }

            if (IsProtectedHexRecord(hexLine))
            {
                continue;
            }

            var offsetAddressArray = hexLine.Skip(1).Take(2).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(offsetAddressArray);
            var offsetAddress = BitConverter.ToUInt16(offsetAddressArray, 0);
            var fullAddress = ((uint)_baseAddress << 16) | offsetAddress;

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

    private static byte[] ConvertLineToBytes(string line)
    {
        var hexLine = new byte[(line.Length - 1) / 2];

        for (var i = 1; i < line.Length; i += 2)
        {
            var hex = line.Substring(i, 2);
            hexLine[(i - 1) / 2] = byte.Parse(hex, NumberStyles.HexNumber);
        }

        return hexLine;
    }

    private bool IsProtectedHexRecord(byte[] hexRecord)
    {
        var offsetAddressArray = hexRecord.Skip(1).Take(2).ToArray();
        var recordType = hexRecord[3];
        var dataArray = hexRecord.Skip(4).Take(hexRecord.Length - 5).ToArray();

        // Extended Linear Address record - update the base address
        if (recordType == 0x04)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(dataArray);
            _baseAddress = BitConverter.ToUInt16(dataArray, 0);
        }
        // Data record - check against protected range
        else if (recordType == 0x00)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(offsetAddressArray);
            var offsetAddress = BitConverter.ToUInt16(offsetAddressArray, 0);
            var hexRecordAddress = ((uint)_baseAddress << 16) | offsetAddress;

            if (hexRecordAddress >= _beginProtectedAddress && hexRecordAddress <= _endProtectedAddress)
            {
                return true;
            }
        }

        return false;
    }
}
