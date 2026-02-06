using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class IntelHexParserTests
{
    private readonly IntelHexParser _parser = new();

    #region ParseHexRecords - Input Validation

    [Fact]
    public void ParseHexRecords_WithNullLines_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.ParseHexRecords(null!));
    }

    [Fact]
    public void ParseHexRecords_WithEmptyArray_ReturnsEmptyList()
    {
        var result = _parser.ParseHexRecords([]);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseHexRecords_WithBlankLines_SkipsThem()
    {
        var lines = new[] { "", "  ", "\t" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseHexRecords_LineWithoutColon_ThrowsInvalidDataException()
    {
        var lines = new[] { "1000000000FF" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
    }

    [Fact]
    public void ParseHexRecords_LineWithEvenCharCount_ThrowsInvalidDataException()
    {
        // A valid line has colon + even number of hex chars = odd total length
        // This has colon + odd hex chars = even total length
        var lines = new[] { ":100000000" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
    }

    [Fact]
    public void ParseHexRecords_TooShortLine_ThrowsInvalidDataException()
    {
        var lines = new[] { ":0000" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
    }

    #endregion

    #region ParseHexRecords - Valid Data

    [Fact]
    public void ParseHexRecords_ValidDataRecord_ParsesCorrectly()
    {
        // :10 0000 00 AABB CCDD EEFF 0011 2233 4455 6677 CS
        // Type 0x10 byte count, address 0x0000, type 0x00 (data)
        var lines = new[] { ":10000000AABBCCDDEEFF00112233445566778899A3" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        var record = result[0];
        Assert.Equal(0x10, record[0]); // byte count
        Assert.Equal(0x00, record[1]); // address high
        Assert.Equal(0x00, record[2]); // address low
        Assert.Equal(0x00, record[3]); // record type (data)
    }

    [Fact]
    public void ParseHexRecords_EofRecord_IncludedInResults()
    {
        // EOF record: :00000001FF
        var lines = new[] { ":00000001FF" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        Assert.Equal(0x01, result[0][3]); // record type = EOF
    }

    [Fact]
    public void ParseHexRecords_MultipleRecords_ParsesAll()
    {
        var lines = new[]
        {
            ":020000041D1EBF",  // Extended address 0x1D1E - BUT this is protected range!
            ":00000001FF"       // EOF
        };

        // Extended address 0x1D1E is in the protected range so it will still be parsed
        // (type 0x04 records aren't filtered, only type 0x00 data records are)
        var result = _parser.ParseHexRecords(lines);

        // Extended address records are not themselves protected, only data records at those addresses
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region ParseHexRecords - Protected Memory Range

    [Fact]
    public void ParseHexRecords_DataInProtectedRange_IsFiltered()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D1EBF",                                   // Extended address 0x1D1E
            ":10000000AABBCCDDEEFF001122334455667788990F",       // Data at 0x1D1E0000 - protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // Extended address record should remain, data record should be filtered, EOF remains
        Assert.Equal(2, result.Count);
        Assert.Equal(0x04, result[0][3]); // extended address
        Assert.Equal(0x01, result[1][3]); // EOF
    }

    [Fact]
    public void ParseHexRecords_DataOutsideProtectedRange_IsKept()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D00C1",                                   // Extended address 0x1D00
            ":10000000AABBCCDDEEFF00112233445566778899A3",       // Data at 0x1D000000 - NOT protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        Assert.Equal(3, result.Count); // All records kept
    }

    [Fact]
    public void ParseHexRecords_DataAtProtectedBoundaryEnd_IsFiltered()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D20A1",                                   // Extended address 0x1D20
            ":10000000AABBCCDDEEFF001122334455667788990F",       // Data at 0x1D200000 - boundary
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // Address 0x1D200000 is at EndProtectedAddress, which is inclusive
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseHexRecords_DataJustAfterProtectedRange_IsKept()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D20A1",                                   // Extended address 0x1D20
            ":10000100AABBCCDDEEFF001122334455667788990F",       // Data at 0x1D200100 - outside
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        Assert.Equal(3, result.Count); // All records kept
    }

    [Fact]
    public void ParseHexRecords_CustomProtectedRange_FiltersCorrectly()
    {
        var parser = new IntelHexParser(0x00010000, 0x00020000);
        var lines = new[]
        {
            ":020000040001FA",                                   // Extended address 0x0001
            ":10000000AABBCCDDEEFF001122334455667788990F",       // Data at 0x00010000 - protected
            ":020000040003F8",                                   // Extended address 0x0003
            ":10000000AABBCCDDEEFF00112233445566778899A3",       // Data at 0x00030000 - NOT protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // First ext addr + second ext addr + second data + EOF = 4
        // First data record (0x00010000) is filtered
        Assert.Equal(4, result.Count);
    }

    #endregion

    #region ParseRecords

    [Fact]
    public void ParseRecords_WithNullLines_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.ParseRecords(null!));
    }

    [Fact]
    public void ParseRecords_ValidDataRecord_ComputesCorrectAddress()
    {
        var lines = new[]
        {
            ":020000041D00C1",                                   // Extended address 0x1D00
            ":10010000AABBCCDDEEFF00112233445566778899A2",       // Data at offset 0x0100
        };

        var result = _parser.ParseRecords(lines);

        Assert.Equal(2, result.Count);
        Assert.Equal(0x1D000100u, result[1].Address); // (0x1D00 << 16) | 0x0100
        Assert.Equal((byte)0x00, result[1].RecordType);
    }

    [Fact]
    public void ParseRecords_ReturnsCorrectRecordTypes()
    {
        var lines = new[]
        {
            ":020000041D00C1",                                   // Extended address (type 0x04)
            ":10000000AABBCCDDEEFF00112233445566778899A3",       // Data (type 0x00)
            ":00000001FF",                                       // EOF (type 0x01)
        };

        var result = _parser.ParseRecords(lines);

        Assert.Equal(3, result.Count);
        Assert.Equal((byte)0x04, result[0].RecordType);
        Assert.Equal((byte)0x00, result[1].RecordType);
        Assert.Equal((byte)0x01, result[2].RecordType);
    }

    #endregion

    #region Hex Parsing Edge Cases

    [Fact]
    public void ParseHexRecords_HexParsingConversion_CorrectBytes()
    {
        // Simple record with known byte values
        // :02 0000 04 1D00 C1
        // Byte count=02, Addr=0000, Type=04, Data=1D00, Checksum=C1
        var lines = new[] { ":020000041D00C1" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        Assert.Equal(0x02, result[0][0]); // byte count
        Assert.Equal(0x00, result[0][1]); // address high
        Assert.Equal(0x00, result[0][2]); // address low
        Assert.Equal(0x04, result[0][3]); // type
        Assert.Equal(0x1D, result[0][4]); // data high
        Assert.Equal(0x00, result[0][5]); // data low
        Assert.Equal(0xC1, result[0][6]); // checksum
    }

    [Fact]
    public void ParseHexRecords_MixedCaseHex_ParsesCorrectly()
    {
        // Intel HEX can have mixed case
        var lines = new[] { ":020000041d00C1" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        Assert.Equal(0x1D, result[0][4]);
    }

    #endregion
}
