namespace Daqifi.Core.Firmware;

/// <summary>
/// Represents a parsed Intel HEX file record with its full linear address and data bytes.
/// </summary>
public class HexFileRecord
{
    /// <summary>
    /// Gets the full 32-bit linear address for this record.
    /// </summary>
    public uint Address { get; }

    /// <summary>
    /// Gets the raw record bytes (byte count, address, type, data, checksum).
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Gets the record type (0x00 = data, 0x01 = EOF, 0x04 = extended linear address).
    /// </summary>
    public byte RecordType { get; }

    /// <summary>
    /// Creates a new HEX file record.
    /// </summary>
    /// <param name="address">The full 32-bit linear address.</param>
    /// <param name="data">The raw record bytes.</param>
    /// <param name="recordType">The record type byte.</param>
    public HexFileRecord(uint address, byte[] data, byte recordType)
    {
        Address = address;
        Data = data;
        RecordType = recordType;
    }
}
