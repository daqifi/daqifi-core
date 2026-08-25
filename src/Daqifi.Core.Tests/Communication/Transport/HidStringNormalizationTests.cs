using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class HidStringNormalizationTests
{
    [Theory]
    // The case from #670: a fixed-width HID descriptor field that is nothing but NUL padding.
    [InlineData("\0")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    // Padding mixed with whitespace, in either order.
    [InlineData(" \0 ")]
    [InlineData("\0 \0")]
    [InlineData("\0\t\0\r\n")]
    public void Normalize_FieldThatIsOnlyPadding_IsReportedAsNoValue(string value)
    {
        Assert.Null(HidStringNormalization.Normalize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_NullEmptyOrWhitespace_IsReportedAsNoValue(string? value)
    {
        Assert.Null(HidStringNormalization.Normalize(value));
    }

    [Theory]
    // Trailing NUL padding is the normal shape of a populated fixed-width descriptor field.
    [InlineData("SN-0042\0\0\0\0\0", "SN-0042")]
    // Padding on the leading edge, and whitespace on the far side of the padding, are stripped too.
    [InlineData("\0\0SN-0042", "SN-0042")]
    [InlineData("  SN-0042  ", "SN-0042")]
    [InlineData(" SN-0042 \0\0", "SN-0042")]
    [InlineData("\0 SN-0042 \0", "SN-0042")]
    [InlineData("\0\0DAQiFi Nyquist 1\0\0", "DAQiFi Nyquist 1")]
    public void Normalize_PaddedField_KeepsOnlyTheValue(string value, string expected)
    {
        Assert.Equal(expected, HidStringNormalization.Normalize(value));
    }

    [Fact]
    public void Normalize_LeavesTheInteriorOfTheValueAlone()
    {
        // Only the padded edges are trimmed. A descriptor whose value legitimately contains
        // interior spacing (a product name) must survive intact, and an interior NUL is not
        // padding either -- silently splicing it out would change the reported identity.
        Assert.Equal("DAQiFi  Nyquist 1", HidStringNormalization.Normalize("\0DAQiFi  Nyquist 1\0"));
        Assert.Equal("SN-1\0SN-2", HidStringNormalization.Normalize("  SN-1\0SN-2  "));
    }
}
