using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class HexFileRecordTests
{
    [Fact]
    public void Constructor_SetsAddress()
    {
        var record = new HexFileRecord(0x1D000100, [0xAA, 0xBB], 0x00);

        Assert.Equal(0x1D000100u, record.Address);
    }

    [Fact]
    public void Constructor_SetsData()
    {
        var data = new byte[] { 0x10, 0x00, 0x00, 0x00, 0xAA, 0xBB };
        var record = new HexFileRecord(0x00000000, data, 0x00);

        Assert.Equal(data, record.Data);
    }

    [Fact]
    public void Constructor_SetsRecordType()
    {
        var record = new HexFileRecord(0x00000000, [0x00], 0x04);

        Assert.Equal((byte)0x04, record.RecordType);
    }

    [Fact]
    public void Constructor_DataRecordType()
    {
        var record = new HexFileRecord(0x1D001000, [0xAA], 0x00);

        Assert.Equal((byte)0x00, record.RecordType);
    }

    [Fact]
    public void Constructor_EofRecordType()
    {
        var record = new HexFileRecord(0x00000000, [0x00, 0x00, 0x00, 0x01, 0xFF], 0x01);

        Assert.Equal((byte)0x01, record.RecordType);
    }
}
