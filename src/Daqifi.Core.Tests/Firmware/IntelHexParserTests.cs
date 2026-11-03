using System.Globalization;
using System.Reflection;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class IntelHexParserTests
{
    private readonly IntelHexParser _parser = new();

    #region ParseHexRecords - Input Validation

    [Fact]
    public void ParseHexRecords_WithNullLines_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => _parser.ParseHexRecords(null!));

        // The guard is eager and names the caller's own parameter. Both matter: if the walk
        // were ever moved behind a lazy iterator that carried the null check with it, the
        // throw would move to first enumeration instead of the call itself.
        Assert.Equal("lines", ex.ParamName);
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
        var lines = new[] { ":100000000" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
    }

    [Fact]
    public void ParseHexRecords_TooShortLine_ThrowsInvalidDataException()
    {
        var lines = new[] { ":0000" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
    }

    [Fact]
    public void ParseHexRecords_ByteCountDisagreesWithDataLength_ThrowsInvalidDataException()
    {
        // A data record whose byte-count field says 1 but which carries 2 data bytes (AA BB).
        // Checksum is valid (01+00+00+00+AA+BB+9A = 0x200 -> 0), so only the byte-count/length
        // check can reject it.
        var lines = new[] { ":01000000AABB9A" };

        var ex = Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
        Assert.Contains("declares", ex.Message);
    }

    [Fact]
    public void ParseHexRecords_Type04RecordWithTooFewDataBytes_ThrowsInvalidDataExceptionNotArgumentException()
    {
        // Type-04 (extended linear address) record declaring 0 data bytes, with a valid checksum
        // (00+00+00+04+FC = 0x100 -> 0). Its byte-count field (0) matches its data length (0), so
        // ValidateRecordLength passes it — but a type-04 record must carry exactly 2 address bytes.
        // Before the fix this reached UpdateBaseAddress and handed BitConverter.ToUInt16 a zero-length
        // array, crashing with a raw ArgumentException instead of the documented InvalidDataException.
        var lines = new[] { ":00000004FC" };

        var ex = Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
        Assert.Contains("type-04", ex.Message);
    }

    [Fact]
    public void ParseRecords_Type04RecordWithTooFewDataBytes_ThrowsInvalidDataExceptionNotArgumentException()
    {
        var lines = new[] { ":00000004FC" };

        Assert.Throws<InvalidDataException>(() => _parser.ParseRecords(lines));
    }

    #endregion

    #region ParseHexRecords - Checksum Validation

    [Fact]
    public void ParseHexRecords_InvalidChecksum_ThrowsInvalidDataException()
    {
        // Valid record is :020000041D00DD, corrupt the checksum
        var lines = new[] { ":020000041D00AA" };

        var ex = Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
        Assert.Contains("invalid checksum", ex.Message);
    }

    [Fact]
    public void ParseHexRecords_InvalidHexCharacters_ThrowsInvalidDataException()
    {
        // 'ZZ' is not valid hex
        var lines = new[] { ":02ZZ00041D00DD" };

        var ex = Assert.Throws<InvalidDataException>(() => _parser.ParseHexRecords(lines));
        Assert.Contains("invalid hex characters", ex.Message);
    }

    #endregion

    #region ParseHexRecords - Valid Data

    [Fact]
    public void ParseHexRecords_ValidDataRecord_ParsesCorrectly()
    {
        var lines = new[] { ":10000000AABBCCDDEEFF00112233445566778899F8" };
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
            ":020000041D1EBF",  // Extended address 0x1D1E
            ":00000001FF"       // EOF
        };

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
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x1D1E0000 - protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

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
            ":020000041D00DD",                                   // Extended address 0x1D00
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x1D000000 - NOT protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // A count alone would also pass if the data record were dropped and some other
        // record kept in its place, so name the record types that survive.
        Assert.Equal<byte[]>([0x04, 0x00, 0x01], RecordTypes(result));
    }

    [Fact]
    public void ParseHexRecords_DataAtProtectedBoundaryEnd_IsFiltered()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D20BD",                                   // Extended address 0x1D20
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x1D200000 - boundary
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // The end of the protected range is inclusive, so the data record at exactly that
        // address goes. The extended-address record and the EOF record stay.
        Assert.Equal<byte[]>([0x04, 0x01], RecordTypes(result));
    }

    [Fact]
    public void ParseHexRecords_DataJustAfterProtectedRange_IsKept()
    {
        var parser = new IntelHexParser();
        var lines = new[]
        {
            ":020000041D20BD",                                   // Extended address 0x1D20
            ":10000100AABBCCDDEEFF00112233445566778899F7",       // Data at 0x1D200100 - outside
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        Assert.Equal<byte[]>([0x04, 0x00, 0x01], RecordTypes(result));
    }

    [Fact]
    public void ParseHexRecords_CustomProtectedRange_FiltersCorrectly()
    {
        var parser = new IntelHexParser(0x00010000, 0x00020000);
        var lines = new[]
        {
            ":020000040001F9",                                   // Extended address 0x0001
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x00010000 - protected
            ":020000040003F7",                                   // Extended address 0x0003
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x00030000 - NOT protected
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseHexRecords(lines);

        // The type order says which data record went: the surviving one follows the second
        // extended-address record, so the dropped one is the first.
        Assert.Equal<byte[]>([0x04, 0x04, 0x00, 0x01], RecordTypes(result));
    }

    private static byte[] RecordTypes(IEnumerable<byte[]> records) =>
        records.Select(r => r[3]).ToArray();

    #endregion

    #region ParseRecords

    [Fact]
    public void ParseRecords_WithNullLines_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => _parser.ParseRecords(null!));

        Assert.Equal("lines", ex.ParamName);
    }

    [Fact]
    public void ParseRecords_WithBlankLines_SkipsThem()
    {
        var lines = new[] { "", "  ", "\t" };

        var result = _parser.ParseRecords(lines);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseRecords_InvalidChecksum_ThrowsInvalidDataException()
    {
        // Valid record is :020000041D00DD, corrupt the checksum.
        var lines = new[] { ":020000041D00AA" };

        var ex = Assert.Throws<InvalidDataException>(() => _parser.ParseRecords(lines));
        Assert.Contains("invalid checksum", ex.Message);
    }

    [Fact]
    public void ParseRecords_DataInProtectedRange_IsFilteredAndBaseAddressStillTracked()
    {
        var lines = new[]
        {
            ":020000041D1EBF",                                   // Extended address 0x1D1E
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x1D1E0000 - protected
            ":020000041D00DD",                                   // Extended address 0x1D00
            ":10010000AABBCCDDEEFF00112233445566778899F7",       // Data at 0x1D000100 - kept
            ":00000001FF"                                        // EOF
        };

        var result = _parser.ParseRecords(lines);

        // The protected data record is dropped, and dropping it must not disturb the running
        // base address that the records after it are addressed against.
        Assert.Equal(4, result.Count);
        Assert.Equal((byte)0x04, result[0].RecordType);
        Assert.Equal((byte)0x04, result[1].RecordType);
        Assert.Equal((byte)0x00, result[2].RecordType);
        Assert.Equal(0x1D000100u, result[2].Address);
        Assert.Equal((byte)0x01, result[3].RecordType);
    }

    [Fact]
    public void ParseRecords_AndParseHexRecords_SelectTheSameRecords()
    {
        // Both public entry points walk, validate and filter identically; they differ only in
        // what they project. Pin that agreement so the two cannot drift apart.
        var lines = new[]
        {
            "",
            ":020000041D1EBF",                                   // Extended address 0x1D1E
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x1D1E0000 - protected
            ":020000041D00DD",                                   // Extended address 0x1D00
            "   ",
            ":10010000AABBCCDDEEFF00112233445566778899F7",       // Data at 0x1D000100 - kept
            ":00000001FF"                                        // EOF
        };

        var hexRecords = _parser.ParseHexRecords(lines);
        var records = _parser.ParseRecords(lines);

        Assert.Equal(hexRecords.Count, records.Count);
        for (var i = 0; i < hexRecords.Count; i++)
        {
            Assert.Equal(hexRecords[i], records[i].Data);
        }
    }

    [Fact]
    public void ParseRecords_ValidDataRecord_ComputesCorrectAddress()
    {
        var lines = new[]
        {
            ":020000041D00DD",                                   // Extended address 0x1D00
            ":10010000AABBCCDDEEFF00112233445566778899F7",       // Data at offset 0x0100
        };

        var result = _parser.ParseRecords(lines);

        Assert.Equal(2, result.Count);
        Assert.Equal(0x1D000100u, result[1].Address);
        Assert.Equal((byte)0x00, result[1].RecordType);
    }

    [Fact]
    public void ParseRecords_ProtectionIsDecidedOnTheSameAddressThatIsReported()
    {
        // Whether a record is filtered and what address it is reported under are the same
        // 32-bit value, computed in two different places from the same two inputs. Pin that
        // they agree, using a protected range whose boundaries fall on the record's OFFSET
        // half: every existing boundary test varies only the extended-address half, so all of
        // them would still pass if the two offset decodes disagreed on byte order.
        var parser = new IntelHexParser(0x00010200, 0x00010300);
        var lines = new[]
        {
            ":020000040001F9",                                   // Extended address 0x0001
            ":10010000AABBCCDDEEFF00112233445566778899F7",       // 0x00010100 - below the range
            ":10020000AABBCCDDEEFF00112233445566778899F6",       // 0x00010200 - range start
            ":10030000AABBCCDDEEFF00112233445566778899F5",       // 0x00010300 - range end (inclusive)
            ":10040000AABBCCDDEEFF00112233445566778899F4",       // 0x00010400 - above the range
            ":00000001FF"                                        // EOF
        };

        var result = parser.ParseRecords(lines);

        var dataAddresses = result.Where(r => r.RecordType == 0x00).Select(r => r.Address).ToArray();
        Assert.Equal([0x00010100u, 0x00010400u], dataAddresses);
    }

    /// <summary>
    /// The protected range is an address interval, so the test that keeps a record out of it has
    /// to be an interval-overlap test. A record carries <c>dataLength</c> bytes from its own
    /// address, and every one of those bytes is programmed. Before issue #839 only the record's
    /// start address was compared against the range, so a record that began below the range and
    /// ran into it was kept in full and its trailing bytes were programmed into protected flash.
    /// </summary>
    /// <remarks>
    /// Every row uses a custom protected range whose bounds sit inside a single 64 KB page. The
    /// default range starts at <c>0x1D1E0000</c>, which is itself a page boundary, so no record
    /// can straddle it without also running past the end of its page - two separate things that
    /// would then be impossible to tell apart. The last two rows cover that page-crossing case on
    /// its own. Both ends of the range are inclusive, which is the convention the shipped default
    /// constants already use.
    /// </remarks>
    [Theory]
    // Rows that do not touch the range.
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0100u, 16, true, "wholly below the range")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x01F0u, 16, true, "last byte is the byte just below the range start")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0301u, 16, true, "starts on the byte just above the inclusive range end")]
    // Rows the start-address test already caught.
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0200u, 16, false, "starts on the range start")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0280u, 16, false, "wholly inside the range")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x02F8u, 16, false, "starts inside the range and runs past its end")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0300u, 16, false, "starts on the inclusive range end")]
    // Rows the start-address test let through. These are the bug in issue #839.
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x01F8u, 16, false, "starts below the range and runs into it")]
    [InlineData(0x00010204u, 0x00010208u, 0x0001u, 0x0200u, 16, false, "starts below the range and spans all of it")]
    // A 16-bit record offset plus the data length can run past the end of the 64 KB page. Such a
    // record is malformed and tools do not agree on it: some carry the data on linearly into the
    // next page, some wrap it to the bottom of the same page. Both readings are tested, so
    // neither can put bytes in the protected range unseen.
    [InlineData(0x00020000u, 0x0002000Fu, 0x0001u, 0xFFF8u, 16, false, "runs past the end of its page, read as carrying on into the next page")]
    [InlineData(0x00010000u, 0x0001000Fu, 0x0001u, 0xFFF8u, 16, false, "runs past the end of its page, read as wrapping to the bottom of the page")]
    // A zero-length data record programs no byte at all, so it has no span. It is judged on its
    // start address, which is the decision this parser has always made for it.
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x0280u, 0, false, "zero data bytes, start address inside the range")]
    [InlineData(0x00010200u, 0x00010300u, 0x0001u, 0x01F8u, 0, true, "zero data bytes, start address below the range")]
    public void ParseRecords_ProtectedRangeTestsTheWholeRecordSpan(
        uint beginProtected,
        uint endProtected,
        uint baseAddress,
        uint offset,
        int dataLength,
        bool expectKept,
        string because)
    {
        var parser = new IntelHexParser(beginProtected, endProtected);
        var lines = new[]
        {
            ExtendedAddressRecordLine((ushort)baseAddress),
            DataRecordLine((ushort)offset, dataLength),
            ":00000001FF"
        };

        var kept = parser.ParseRecords(lines).Any(r => r.RecordType == 0x00);

        var start = (baseAddress << 16) | offset;
        Assert.True(
            kept == expectKept,
            $"A record of {dataLength} byte(s) at 0x{start:X8} ({because}), against the protected "
            + $"range 0x{beginProtected:X8}-0x{endProtected:X8}, should be "
            + $"{(expectKept ? "kept" : "dropped")} but was {(kept ? "kept" : "dropped")}.");
    }

    /// <summary>
    /// Builds a type-00 data record line with the given 16-bit offset and data length, with a
    /// correct byte count and checksum. Built here rather than written out by hand because the
    /// span cases need many lengths and offsets, and a hand-computed checksum is easy to get
    /// wrong - a wrong one would throw instead of testing the protected-range decision.
    /// </summary>
    private static string DataRecordLine(ushort offset, int dataLength)
    {
        var bytes = new List<byte> { (byte)dataLength, (byte)(offset >> 8), (byte)(offset & 0xFF), 0x00 };
        for (var i = 0; i < dataLength; i++)
        {
            bytes.Add((byte)(i & 0xFF));
        }

        return BuildLine(bytes);
    }

    /// <summary>
    /// Builds a type-04 extended-linear-address record line carrying the given upper 16 bits.
    /// </summary>
    private static string ExtendedAddressRecordLine(ushort baseAddress)
    {
        var bytes = new List<byte> { 0x02, 0x00, 0x00, 0x04, (byte)(baseAddress >> 8), (byte)(baseAddress & 0xFF) };
        return BuildLine(bytes);
    }

    private static string BuildLine(List<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        // Intel HEX checksum: the two's complement of the sum of every byte before it.
        bytes.Add((byte)(-sum & 0xFF));

        return ":" + string.Concat(bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ParseRecords_ReturnsCorrectRecordTypes()
    {
        var lines = new[]
        {
            ":020000041D00DD",                                   // Extended address (type 0x04)
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data (type 0x00)
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
        // :02 0000 04 1D00 DD
        var lines = new[] { ":020000041D00DD" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        Assert.Equal(0x02, result[0][0]); // byte count
        Assert.Equal(0x00, result[0][1]); // address high
        Assert.Equal(0x00, result[0][2]); // address low
        Assert.Equal(0x04, result[0][3]); // type
        Assert.Equal(0x1D, result[0][4]); // data high
        Assert.Equal(0x00, result[0][5]); // data low
        Assert.Equal(0xDD, result[0][6]); // checksum
    }

    [Fact]
    public void ParseHexRecords_MixedCaseHex_ParsesCorrectly()
    {
        var lines = new[] { ":020000041d00DD" };
        var result = _parser.ParseHexRecords(lines);

        Assert.Single(result);
        Assert.Equal(0x1D, result[0][4]);
    }

    #endregion

    #region Default Protected Range Constants

    /// <summary>
    /// The SCREAMING_SNAKE spellings shipped on the public surface before #484 renamed them are
    /// kept as forwarders so that source written against the released package still compiles -
    /// the source compatibility ADR 0002 promises - and marked
    /// <see cref="ObsoleteAttribute" /> so a recompile says which name to move to.
    /// </summary>
    /// <remarks>
    /// The obsoletion is the only part of these two constants that needs a test. Everything else
    /// about them is already a build error: PublicAPI.Shipped.txt records a public const by
    /// <b>value</b>, so changing either literal - or letting a forwarder drift from the constant
    /// it forwards to - fails RS0016/RS0017, and deleting one fails RS0017 as a removed entry.
    /// Both were written, mutation-tested, and dropped for turning the build red rather than
    /// failing on a green one. <c>[Obsolete]</c> is not recorded in those files, so nothing but
    /// this notices if it is removed and the rename quietly stops telling anyone.
    /// </remarks>
    [Theory]
    [InlineData("DEFAULT_BEGIN_PROTECTED_ADDRESS")]
    [InlineData("DEFAULT_END_PROTECTED_ADDRESS")]
    public void ObsoleteProtectedRangeConstant_IsStillPublicAndMarkedObsolete(string name)
    {
        // Read by reflection rather than by name so that removing the field fails here as a
        // missing member naming the promise, rather than as a compile error in this file.
        var field = typeof(IntelHexParser).GetField(name, BindingFlags.Public | BindingFlags.Static);

        Assert.True(field != null,
            $"IntelHexParser.{name} was removed. It is a shipped public constant; dropping it "
            + "breaks source compatibility (ADR 0002) and needs a major version.");
        Assert.NotNull(field!.GetCustomAttribute<ObsoleteAttribute>());
    }

    #endregion
}
