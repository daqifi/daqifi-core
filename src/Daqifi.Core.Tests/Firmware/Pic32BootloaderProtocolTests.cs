using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Pic32BootloaderProtocolTests
{
    private readonly Pic32BootloaderProtocol _protocol = new();

    [Fact]
    public void ImplementsIBootloaderProtocol()
    {
        Assert.IsAssignableFrom<IBootloaderProtocol>(_protocol);
    }

    [Fact]
    public void Constructor_WithCustomProtectedRange_UsesCustomRange()
    {
        var protocol = new Pic32BootloaderProtocol(0x00010000, 0x00020000);

        var lines = new[]
        {
            ":020000040001F9",                                   // Extended address 0x0001
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x00010000 - protected
            ":00000001FF"                                        // EOF
        };

        var result = protocol.ParseHexFile(lines);

        // Data record should be filtered by custom range
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ComputeCrcRegions_ContiguousRecords_SingleRegionWithKseg0AddressAndCrc()
    {
        var lines = new[]
        {
            ":020000041D00DD",                  // base 0x1D000000
            ":080000000001020304050607DC",      // 8 bytes @ 0x1D000000 (00..07)
            ":0800080008090A0B0C0D0E0F94",      // 8 bytes @ 0x1D000008 (08..0F)
            ":00000001FF"                       // EOF
        };

        var regions = _protocol.ComputeCrcRegions(lines);

        var region = Assert.Single(regions);
        // Physical 0x1D000000 → KSEG0 0x9D000000 (the address READ_CRC expects).
        Assert.Equal(0x9D000000u, region.Address);
        Assert.Equal(16u, region.Length);
        var expectedCrc = new Crc16(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()).Crc;
        Assert.Equal(expectedCrc, region.ExpectedCrc);
    }

    [Fact]
    public void ComputeCrcRegions_NonContiguousRecords_ProducesSeparateRegions()
    {
        var lines = new[]
        {
            ":020000041D00DD",                  // base 0x1D000000
            ":080000000001020304050607DC",      // 8 bytes @ 0x1D000000
            ":0800100010111213141516174C",      // 8 bytes @ 0x1D000010 (gap at 0x08..0x0F)
            ":00000001FF"
        };

        var regions = _protocol.ComputeCrcRegions(lines);

        Assert.Equal(2, regions.Count);
        Assert.Equal(0x9D000000u, regions[0].Address);
        Assert.Equal(8u, regions[0].Length);
        Assert.Equal(0x9D000010u, regions[1].Address);
        Assert.Equal(8u, regions[1].Length);
    }

    [Fact]
    public void ComputeCrcRegions_ExcludesProtectedCalibrationRecords()
    {
        // The bootloader never programs the protected calibration range, so its
        // flash retains old contents — including it in a CRC region would make
        // verification fail on every real device. It must be excluded.
        var lines = new[]
        {
            ":020000041D00DD",                  // base 0x1D000000
            ":080000000001020304050607DC",      // 8 bytes @ 0x1D000000 — normal
            ":020000041D1EBF",                  // base 0x1D1E0000 (protected range)
            ":080000000001020304050607DC",      // 8 bytes @ 0x1D1E0000 — protected, excluded
            ":00000001FF"
        };

        var regions = _protocol.ComputeCrcRegions(lines);

        var region = Assert.Single(regions);
        Assert.Equal(0x9D000000u, region.Address);
        Assert.Equal(8u, region.Length);
    }

    [Fact]
    public void ComputeCrcRegions_ExcludesRecordsOutsideAppFlashRange()
    {
        // Real firmware HEX files carry linker-emitted data outside the app-flash
        // partition (e.g. at physical 0x00000000). The bootloader never programs
        // those records, so including them would compare against flash the device
        // never wrote and fail verification. They must be excluded.
        var lines = new[]
        {
            ":020000040000FA",                  // base 0x00000000 (outside app flash)
            ":080000000001020304050607DC",      // 8 bytes @ 0x00000000 — excluded
            ":020000041D00DD",                  // base 0x1D000000 (app flash)
            ":080000000001020304050607DC",      // 8 bytes @ 0x1D000000 — kept
            ":00000001FF"
        };

        var regions = _protocol.ComputeCrcRegions(lines);

        var region = Assert.Single(regions);
        Assert.Equal(0x9D000000u, region.Address);
        Assert.Equal(8u, region.Length);
    }

    [Fact]
    public void ComputeCrcRegions_ExcludesRecordSpanningPastAppFlashEnd()
    {
        // A record whose start is in-window but whose byte span crosses the
        // app-flash end (0x1D1FFFFF). The bootloader programs per byte and skips
        // the out-of-window tail, so CRC'ing the whole record would mismatch.
        // A custom protected range (elsewhere) keeps the protected-range filter
        // out of the way, isolating the app-flash-span check.
        var protocol = new Pic32BootloaderProtocol(0x00010000, 0x00020000);
        var lines = new[]
        {
            ":020000041D1FBE",                            // base 0x1D1F0000
            // 16 bytes @ 0x1D1FFFF8 -> spans 0x1D1FFFF8..0x1D200007 (past 0x1D1FFFFF)
            ":10FFF800000102030405060708090A0B0C0D0E0F81",
            ":00000001FF"
        };

        var regions = protocol.ComputeCrcRegions(lines);

        Assert.Empty(regions);
    }
}
