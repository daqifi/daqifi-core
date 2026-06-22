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
    public void CreateRequestVersionMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();
        var result = _protocol.CreateRequestVersionMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateEraseFlashMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateEraseFlashMessage();
        var result = _protocol.CreateEraseFlashMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateProgramFlashMessage_DelegatesToProducer()
    {
        var hexRecord = new byte[] { 0xAA, 0xBB };
        var expected = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);
        var result = _protocol.CreateProgramFlashMessage(hexRecord);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateJumpToApplicationMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage();
        var result = _protocol.CreateJumpToApplicationMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeVersionResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x10, 0x01, 0x02, 0x05 };
        var expected = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);
        var result = _protocol.DecodeVersionResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeProgramFlashResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x03 };
        var expected = Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(data);
        var result = _protocol.DecodeProgramFlashResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeEraseFlashResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x02 };
        var expected = Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(data);
        var result = _protocol.DecodeEraseFlashResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseHexFile_DelegatesToParser()
    {
        var lines = new[] { ":020000041D00DD", ":00000001FF" };
        var result = _protocol.ParseHexFile(lines);

        Assert.Equal(2, result.Count);
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
    public void CreateReadCrcMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateReadCrcMessage(0x9D000000, 0x200000);
        var result = _protocol.CreateReadCrcMessage(0x9D000000, 0x200000);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeReadCrcResponse_DelegatesToConsumer()
    {
        var framed = FrameReadCrcResponse(0xABCD);
        var expected = Pic32BootloaderMessageConsumer.DecodeReadCrcResponse(framed);
        var result = _protocol.DecodeReadCrcResponse(framed);

        Assert.Equal(expected, result);
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

    private static byte[] FrameReadCrcResponse(ushort flashCrc)
    {
        const byte soh = 0x01;
        const byte eot = 0x04;
        const byte dle = 0x10;

        byte[] content = [0x04, (byte)(flashCrc & 0xFF), (byte)(flashCrc >> 8)];
        var frameCrc = new Crc16(content).Crc;
        var body = new List<byte>(content) { (byte)(frameCrc & 0xFF), (byte)(frameCrc >> 8) };

        var framed = new List<byte> { soh };
        foreach (var b in body)
        {
            if (b is soh or eot or dle)
            {
                framed.Add(dle);
            }
            framed.Add(b);
        }
        framed.Add(eot);
        return framed.ToArray();
    }
}
