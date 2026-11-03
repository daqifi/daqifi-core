using Daqifi.Core.Firmware.Winc;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// The exact command line that launches the WINC flash tool (issue #844).
/// </summary>
/// <remarks>
/// <para>
/// Every case asserts the whole string, character for character. A substring check passes on a
/// line whose quotes sit in the wrong places, which is the whole defect here, so there are no
/// substring checks below.
/// </para>
/// <para>
/// The builder is a pure function and takes the "run through cmd.exe" decision as an argument,
/// so both rules run on every platform. That matters, because the Windows rule is the broken one
/// and the machines that develop this library are mostly not Windows.
/// </para>
/// <para>
/// <b>Read every apostrophe below as a double quote.</b> Each case writes <c>'</c> and
/// <see cref="Q"/> turns it into <c>"</c>, in the inputs and in the expected lines alike. What
/// these cases are about is how many quotes land where, and a C# literal full of <c>""</c> and
/// <c>\"</c> hides exactly that. No case needs a real apostrophe, so nothing is lost.
/// </para>
/// </remarks>
public class WincFlashCommandLineTests
{
    private const string PlainToolPath = @"C:\tools\winc\winc_flash_tool.cmd";
    private const string SpacedToolPath = @"C:\Program Files\winc tool\winc_flash_tool.cmd";
    private const string DirectToolPath = @"C:\tools\winc\winc_flash_tool.exe";
    private const string Template = "/p {port} /w";
    private const string PortAndPathTemplate = "/p {port} /f {firmwarePath} /w";
    private const string FirmwarePath = @"C:\fw";

    /// <summary>Reads every <c>'</c> as a <c>"</c>. See the note on this class.</summary>
    private static string Q(string text) => text.Replace('\'', '"');

    // -------------------------------------------------------------------------------------------
    // Through cmd.exe — the Windows .cmd/.bat rule
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole line, for each shape of tool path and each shape of value. The outer quote pair
    /// is always there, because <c>/s</c> always strips exactly one pair.
    /// </summary>
    [Theory]
    // A plain path and a plain port: the ordinary case, and it must stay boring.
    [InlineData(
        PlainToolPath,
        "COM7",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p COM7 /w'")]
    // A path with a space. This is the case the issue reports: an ordinary Windows path.
    [InlineData(
        SpacedToolPath,
        "COM7",
        @"/d /s /v:off /c ''C:\Program Files\winc tool\winc_flash_tool.cmd' /p COM7 /w'")]
    // A value with a space, so cmd.exe reads it as one token.
    [InlineData(
        PlainToolPath,
        "COM 42",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM 42' /w'")]
    // A value with an ampersand. The quotes make it data. Without them cmd.exe runs a second
    // command.
    [InlineData(
        PlainToolPath,
        "COM7&calc",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM7&calc' /w'")]
    // The other cmd.exe operators, for the same reason.
    [InlineData(
        PlainToolPath,
        "COM7|calc",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM7|calc' /w'")]
    [InlineData(
        PlainToolPath,
        "COM7>out.txt",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM7>out.txt' /w'")]
    [InlineData(
        PlainToolPath,
        "COM7^7",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM7^7' /w'")]
    [InlineData(
        PlainToolPath,
        "COM;7",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM;7' /w'")]
    // An empty value: an empty quoted token. The tool still gets an argument there, instead of
    // reading the next switch as the port.
    [InlineData(
        PlainToolPath,
        "",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p '' /w'")]
    // An exclamation mark stays as written. That is what /v:off is for.
    [InlineData(
        PlainToolPath,
        "COM!7",
        @"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p COM!7 /w'")]
    public void ThroughCmd_BuildsTheWholeLine(string toolPath, string port, string expected)
    {
        var command = WincFlashCommandLine.Build(
            toolPath,
            Template,
            Q(port),
            FirmwarePath,
            throughWindowsShell: true);

        Assert.Equal("cmd.exe", command.FileName);
        Assert.Equal(Q(expected), command.Arguments);
    }

    [Fact]
    public void ThroughCmd_QuotesAFirmwarePathWithASpace()
    {
        var command = WincFlashCommandLine.Build(
            PlainToolPath,
            PortAndPathTemplate,
            "COM7",
            @"C:\Users\Chris\My Firmware",
            throughWindowsShell: true);

        Assert.Equal(
            Q(@"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p COM7 /f 'C:\Users\Chris\My Firmware' /w'"),
            command.Arguments);
    }

    /// <summary>
    /// The case the issue measured on a real <c>cmd.exe</c>: a space in the tool path and a space
    /// in the value.
    /// </summary>
    [Fact]
    public void ThroughCmd_WithASpaceInBothThePathAndTheValue_KeepsEveryQuoteInPlace()
    {
        var command = WincFlashCommandLine.Build(
            SpacedToolPath,
            Template,
            "COM 42",
            FirmwarePath,
            throughWindowsShell: true);

        // The old line held four quotes and no outer pair, so cmd.exe stripped the first and the
        // last and cut the quotes off the tool path. For the record, with ' read as " again:
        //   built: /c 'C:\Program Files\winc tool\winc_flash_tool.cmd' /p 'COM 42' /w
        //   ran:   C:\Program          ...and reported that it is not recognized
        Assert.Equal(
            Q(@"/d /s /v:off /c ''C:\Program Files\winc tool\winc_flash_tool.cmd' /p 'COM 42' /w'"),
            command.Arguments);
    }

    // -------------------------------------------------------------------------------------------
    // Through cmd.exe — the values that are refused
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("CO'M7", "double quote")]
    [InlineData("'", "double quote")]
    [InlineData("%PATH%", "percent sign")]
    [InlineData("COM7%", "percent sign")]
    [InlineData("COM\n7", "control character")]
    public void ThroughCmd_RefusesAValueItCannotEncode(string port, string expectedCharacter)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => WincFlashCommandLine.Build(
                PlainToolPath,
                Template,
                Q(port),
                FirmwarePath,
                throughWindowsShell: true));

        // The message says which value, what is wrong with it, and that nothing was changed
        // behind the caller's back.
        Assert.Contains("serial port", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedCharacter, error.Message, StringComparison.Ordinal);
        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\to'ols\winc_flash_tool.cmd")]
    [InlineData(@"C:\%TEMP%\winc_flash_tool.cmd")]
    public void ThroughCmd_RefusesAToolPathItCannotEncode(string toolPath)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => WincFlashCommandLine.Build(
                Q(toolPath),
                Template,
                "COM7",
                FirmwarePath,
                throughWindowsShell: true));

        Assert.Contains("flash tool path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThroughCmd_RefusesAFirmwarePathItCannotEncode()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => WincFlashCommandLine.Build(
                PlainToolPath,
                PortAndPathTemplate,
                "COM7",
                @"C:\fw\100%",
                throughWindowsShell: true));

        Assert.Contains("firmware path", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Directly — the ordinary Windows argument rule
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A tool started directly gets no shell, so there is no shell syntax to guard against and
    /// nothing is refused. A quote is encoded here, because the argument rules have an escape for
    /// it.
    /// </summary>
    [Theory]
    // Plain.
    [InlineData("COM7", "/p COM7 /w")]
    // A space, so the value is wrapped.
    [InlineData("COM 42", "/p 'COM 42' /w")]
    // A quote, so the value is wrapped and the quote is escaped.
    [InlineData("CO'M7", @"/p 'CO\'M7' /w")]
    // An ampersand. No shell reads this line, so it needs no quotes.
    [InlineData("COM7&calc", "/p COM7&calc /w")]
    // A percent sign, for the same reason.
    [InlineData("%PATH%", "/p %PATH% /w")]
    // Empty.
    [InlineData("", "/p '' /w")]
    public void Directly_BuildsTheWholeLine(string port, string expected)
    {
        var command = WincFlashCommandLine.Build(
            DirectToolPath,
            Template,
            Q(port),
            FirmwarePath,
            throughWindowsShell: false);

        // The tool path is the program name, not part of a command line, so it carries no quotes.
        Assert.Equal(DirectToolPath, command.FileName);
        Assert.Equal(Q(expected), command.Arguments);
    }

    [Theory]
    // A path with a space is wrapped, and its backslashes stay single.
    [InlineData(@"C:\Users\Chris\My Firmware", @"/p COM7 /f 'C:\Users\Chris\My Firmware' /w")]
    // A path that ends in a backslash and needs wrapping. That last backslash is doubled, or it
    // escapes the closing quote and swallows the end of the argument.
    [InlineData(@"C:\My Firmware\", @"/p COM7 /f 'C:\My Firmware\\' /w")]
    // A path with no space keeps its backslashes as written, because it needs no quotes.
    [InlineData(@"C:\fw\", @"/p COM7 /f C:\fw\ /w")]
    public void Directly_QuotesAFirmwarePathByTheArgumentRules(string firmwarePath, string expected)
    {
        var command = WincFlashCommandLine.Build(
            DirectToolPath,
            PortAndPathTemplate,
            "COM7",
            firmwarePath,
            throughWindowsShell: false);

        Assert.Equal(Q(expected), command.Arguments);
    }

    [Fact]
    public void Directly_RefusesAValueWithAControlCharacter()
    {
        // The one thing no command line can carry, under either rule.
        var error = Assert.Throws<InvalidOperationException>(
            () => WincFlashCommandLine.Build(
                DirectToolPath,
                Template,
                "COM\r7",
                FirmwarePath,
                throughWindowsShell: false));

        Assert.Contains("control character", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // The template itself
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ATemplateWithoutTheFirmwarePlaceholder_SubstitutesOnlyThePort()
    {
        // The default template. The WINC script finds its own firmware in its working directory.
        var command = WincFlashCommandLine.Build(
            PlainToolPath,
            "/p {port} /d WINC1500 /k /e /i aio /w",
            "COM7",
            FirmwarePath,
            throughWindowsShell: true);

        Assert.Equal(
            Q(@"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p COM7 /d WINC1500 /k /e /i aio /w'"),
            command.Arguments);
    }

    [Fact]
    public void APlaceholderUsedTwice_IsSubstitutedBothTimes()
    {
        var command = WincFlashCommandLine.Build(
            PlainToolPath,
            "/p {port} /also {port}",
            "COM 42",
            FirmwarePath,
            throughWindowsShell: true);

        Assert.Equal(
            Q(@"/d /s /v:off /c ''C:\tools\winc\winc_flash_tool.cmd' /p 'COM 42' /also 'COM 42''"),
            command.Arguments);
    }
}
