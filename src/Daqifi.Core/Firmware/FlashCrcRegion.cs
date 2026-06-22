namespace Daqifi.Core.Firmware;

/// <summary>
/// Describes a contiguous region of programmed application flash whose contents
/// can be independently verified against the device using the bootloader
/// <c>READ_CRC</c> (0x04) command.
/// </summary>
/// <remarks>
/// The host computes <see cref="ExpectedCrc"/> over the same bytes the device
/// just programmed; after programming, it asks the bootloader for the CRC-16 of
/// the same <see cref="Address"/>/<see cref="Length"/> range and compares. A
/// mismatch means the flash does not match the firmware image (bit flip,
/// partially-programmed record, etc.).
/// </remarks>
public sealed class FlashCrcRegion
{
    /// <summary>
    /// Gets the KSEG0 virtual flash address to send in the <c>READ_CRC</c>
    /// command. The firmware applies <c>KVA0_TO_KVA1</c> to this value before
    /// reading flash, so it must be the KSEG0 form (physical address with bit
    /// 31 set), matching the address the bootloader programmed the record to.
    /// </summary>
    public uint Address { get; }

    /// <summary>
    /// Gets the number of contiguous flash bytes covered by this region.
    /// </summary>
    public uint Length { get; }

    /// <summary>
    /// Gets the host-computed CRC-16/XMODEM over the region's bytes, in the same
    /// form the bootloader returns from <c>READ_CRC</c>.
    /// </summary>
    public ushort ExpectedCrc { get; }

    /// <summary>
    /// Creates a new flash CRC region descriptor.
    /// </summary>
    /// <param name="address">KSEG0 virtual flash address of the first byte.</param>
    /// <param name="length">Number of contiguous bytes in the region.</param>
    /// <param name="expectedCrc">Host-computed CRC-16/XMODEM over the region.</param>
    public FlashCrcRegion(uint address, uint length, ushort expectedCrc)
    {
        Address = address;
        Length = length;
        ExpectedCrc = expectedCrc;
    }
}
