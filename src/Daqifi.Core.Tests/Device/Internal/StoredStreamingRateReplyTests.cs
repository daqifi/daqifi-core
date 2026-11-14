using System;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Tests for <see cref="StoredStreamingRateReply"/>: finding the <c>SYSTem:STReam:START?</c>
/// answer in the capability exchange it shares with the schema version and the document.
/// </summary>
public class StoredStreamingRateReplyTests
{
    private const string Document = "{\"schema_version\":2}";

    /// <summary>Finds the document the way the device does, then the rate after it.</summary>
    private static bool TryParse(string[] lines, out int rateHz) =>
        StoredStreamingRateReply.TryParse(lines, StoredStreamingRateReply.FindCapabilityDocument(lines, out _), out rateHz);

    [Fact]
    public void TryParse_TheLineAfterTheDocument_IsTheRate()
    {
        Assert.True(TryParse(new[] { "2", Document, "1000" }, out var rateHz));
        Assert.Equal(1000, rateHz);
    }

    [Fact]
    public void TryParse_TheBootPlaceholder_IsRead()
    {
        // The parser reads 1 Hz like any other value; whether to adopt it is decided where the
        // rate is kept (it is not: StreamingSessionController.FirmwareBootDefaultStoredRateHz).
        Assert.True(TryParse(new[] { "2", Document, "1" }, out var rateHz));
        Assert.Equal(1, rateHz);
    }

    [Fact]
    public void TryParse_SurroundingWhitespaceAndBlankLines_AreIgnored()
    {
        Assert.True(TryParse(new[] { "2", "  " + Document, "", "  500\r" }, out var rateHz));
        Assert.Equal(500, rateHz);
    }

    [Fact]
    public void TryParse_WhenTheSchemaVersionIsMissing_StillFindsTheRateAfterTheDocument()
    {
        Assert.True(TryParse(new[] { Document, "250" }, out var rateHz));
        Assert.Equal(250, rateHz);
    }

    [Fact]
    public void TryParse_WithNoDocument_IsRefused()
    {
        // Two bare integers with no anchor could be the version and the rate in either order.
        Assert.False(TryParse(new[] { "2", "1000" }, out var rateHz));
        Assert.Equal(0, rateHz);
    }

    [Fact]
    public void TryParse_WithNothingAfterTheDocument_IsRefused()
    {
        Assert.False(TryParse(new[] { "2", Document }, out _));
    }

    [Fact]
    public void TryParse_WithNoLinesAtAll_IsRefused()
    {
        Assert.False(TryParse(Array.Empty<string>(), out _));
    }

    [Theory]
    [InlineData("**ERROR: -113, \"Undefined header\"")] // firmware that does not know the query
    [InlineData("-113,\"Undefined header\"")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("10 00")]
    [InlineData("99999999999")] // a UInt64 the firmware could report, but no int rate
    public void TryParse_WhenTheLineAfterTheDocumentIsNotAPlainUnsignedInteger_IsRefused(string reply)
    {
        Assert.False(TryParse(new[] { "2", Document, reply }, out var rateHz));
        Assert.Equal(0, rateHz);
    }

    [Fact]
    public void FindCapabilityDocument_IsTheFirstLineTheCapabilityParserAccepts()
    {
        Assert.Equal(1, StoredStreamingRateReply.FindCapabilityDocument(new[] { "2", "  " + Document, "1" }, out var document));
        Assert.Equal(2, document!.SchemaVersion);
        Assert.Equal(-1, StoredStreamingRateReply.FindCapabilityDocument(new[] { "2", "1" }, out document));
        Assert.Null(document);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"event\":\"noise\"}")]
    [InlineData("{\"schema_version\":")] // a truncated object
    public void FindCapabilityDocument_SkipsJsonThatIsNotACapabilityDocument(string unrelated)
    {
        Assert.Equal(2, StoredStreamingRateReply.FindCapabilityDocument(new[] { "2", unrelated, Document, "500" }, out _));
    }

    [Fact]
    public void TryParse_UnrelatedJsonAheadOfTheDocument_DoesNotMoveTheAnchor()
    {
        // The first line that opens an object is not necessarily the document. The rate follows
        // the document the parser accepted, so "{}" here is not taken for it and 500 is read.
        Assert.True(TryParse(new[] { "2", "{}", Document, "500" }, out var rateHz));
        Assert.Equal(500, rateHz);
    }

    [Fact]
    public void TryParse_WhenNoLineIsACapabilityDocument_IsRefused()
    {
        Assert.False(TryParse(new[] { "2", "{\"event\":\"noise\"}", "500" }, out var rateHz));
        Assert.Equal(0, rateHz);
    }

    [Fact]
    public void TryParse_AnErrorLineAfterTheDocument_IsNotSkippedToALaterInteger()
    {
        // The first thing after the document is the answer to the rate query. A later integer is
        // not that answer, however plausible it looks.
        Assert.False(TryParse(
            new[] { "2", Document, "**ERROR: -113, \"Undefined header\"", "1000" },
            out _));
    }
}
