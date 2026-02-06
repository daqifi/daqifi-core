namespace Daqifi.Core.Firmware;

/// <summary>
/// Computes a CRC-16/XMODEM checksum using a 4-bit lookup table.
/// </summary>
public class Crc16
{
    private static readonly ushort[] TABLE =
    [
        0x0000, 0x1021, 0x2042, 0x3063, 0x4084, 0x50A5, 0x60C6, 0x70E7,
        0x8108, 0x9129, 0xA14A, 0xB16B, 0xC18C, 0xD1AD, 0xE1CE, 0xF1EF
    ];

    /// <summary>
    /// Gets the computed 16-bit CRC value.
    /// </summary>
    public ushort Crc { get; }

    /// <summary>
    /// Gets the low byte of the CRC.
    /// </summary>
    public byte Low => (byte)(Crc & 0xFF);

    /// <summary>
    /// Gets the high byte of the CRC.
    /// </summary>
    public byte High => (byte)(Crc >> 8);

    /// <summary>
    /// Computes the CRC-16/XMODEM checksum for the specified data.
    /// </summary>
    /// <param name="data">The data to compute the checksum for.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    public Crc16(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Crc = Calculate(data);
    }

    private static ushort Calculate(byte[] data)
    {
        ushort crc = 0;
        foreach (var item in data)
        {
            var i = (uint)((crc >> 12) ^ (item >> 4));
            crc = (ushort)(TABLE[i & 0x0F] ^ (crc << 4));
            i = (uint)((crc >> 12) ^ (item >> 0));
            crc = (ushort)(TABLE[i & 0x0F] ^ (crc << 4));
        }
        return crc;
    }
}
