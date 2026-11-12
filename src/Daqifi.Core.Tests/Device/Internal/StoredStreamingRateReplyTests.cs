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

    [Fact]
    public void TryParse_TheLineAfterTheDocument_IsTheRate()
    {
        Assert.True(StoredStreamingRateReply.TryParse(new[] { "2", Document, "1000" }, out var rateHz));
        Assert.Equal(1000, rateHz);
    }

    [Fact]
    public void TryParse_TheBootPlaceholder_IsRead()
    {
        // The parser reads 1 Hz like any other value; whether to adopt it is decided where the
        // rate is kept (it is not: StreamingSessionController.FirmwareBootDefaultStoredRateHz).
        Assert.True(StoredStreamingRateReply.TryParse(new[] { "2", Document, "1" }, out var rateHz));
        Assert.Equal(1, rateHz);
    }

    [Fact]
    public void TryParse_SurroundingWhitespaceAndBlankLines_AreIgnored()
    {
        Assert.True(StoredStreamingRateReply.TryParse(new[] { "2", "  " + Document, "", "  500\r" }, out var rateHz));
        Assert.Equal(500, rateHz);
    }

    [Fact]
    public void TryParse_WhenTheSchemaVersionIsMissing_StillFindsTheRateAfterTheDocument()
    {
        Assert.True(StoredStreamingRateReply.TryParse(new[] { Document, "250" }, out var rateHz));
        Assert.Equal(250, rateHz);
    }

    [Fact]
    public void TryParse_WithNoDocument_IsRefused()
    {
        // Two bare integers with no anchor could be the version and the rate in either order.
        Assert.False(StoredStreamingRateReply.TryParse(new[] { "2", "1000" }, out var rateHz));
        Assert.Equal(0, rateHz);
    }

    [Fact]
    public void TryParse_WithNothingAfterTheDocument_IsRefused()
    {
        Assert.False(StoredStreamingRateReply.TryParse(new[] { "2", Document }, out _));
    }

    [Fact]
    public void TryParse_WithNoLinesAtAll_IsRefused()
    {
        Assert.False(StoredStreamingRateReply.TryParse(Array.Empty<string>(), out _));
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
        Assert.False(StoredStreamingRateReply.TryParse(new[] { "2", Document, reply }, out var rateHz));
        Assert.Equal(0, rateHz);
    }

    [Theory]
    [InlineData("{\"streaming\":{\"timing\":{\"actual_rate_millihz\":0}}}", false)]
    [InlineData("{\"streaming\":{\"timing\":{\"actual_rate_millihz\":1000}}}", true)]
    [InlineData("{\"streaming\":{\"timing\":{\"actual_rate_millihz\":10000,\"clock_ok\":true}}}", true)]
    public void ReadRateConfigured_ReadsTheDocumentsActualRate(string document, bool expected)
    {
        Assert.Equal(expected, StoredStreamingRateReply.ReadRateConfigured(new[] { "2", document, "1" }));
    }

    [Theory]
    [InlineData("{\"streaming\":{}}")]                                           // no timing block (before 3.8.0)
    [InlineData("{\"streaming\":{\"timing\":{}}}")]                              // no actual rate
    [InlineData("{\"streaming\":{\"timing\":{\"actual_rate_millihz\":\"x\"}}}")]   // not a number
    [InlineData("{\"streaming\":{\"timing\":{\"actual_rate_millihz\":1.5}}}")]   // not an integer
    [InlineData("{\"streaming\":{\"timing\":[]}}")]
    [InlineData("{\"streaming\":")]                                              // not valid JSON
    public void ReadRateConfigured_WhenTheDocumentDoesNotSay_IsUnknown(string document)
    {
        Assert.Null(StoredStreamingRateReply.ReadRateConfigured(new[] { "2", document, "1" }));
    }

    [Fact]
    public void ReadRateConfigured_WithNoDocument_IsUnknown()
    {
        Assert.Null(StoredStreamingRateReply.ReadRateConfigured(new[] { "2", "1" }));
    }

    [Fact]
    public void IndexOfDocumentLine_IsTheFirstLineThatOpensAnObject()
    {
        Assert.Equal(1, StoredStreamingRateReply.IndexOfDocumentLine(new[] { "2", "  " + Document, "1" }));
        Assert.Equal(-1, StoredStreamingRateReply.IndexOfDocumentLine(new[] { "2", "1" }));
    }

    [Fact]
    public void TryParse_AnErrorLineAfterTheDocument_IsNotSkippedToALaterInteger()
    {
        // The first thing after the document is the answer to the rate query. A later integer is
        // not that answer, however plausible it looks.
        Assert.False(StoredStreamingRateReply.TryParse(
            new[] { "2", Document, "**ERROR: -113, \"Undefined header\"", "1000" },
            out _));
    }
}
