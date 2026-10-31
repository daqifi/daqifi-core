using System.Text;

namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// The program name and the command line that launch Microchip's WINC flash tool.
/// </summary>
/// <param name="FileName">The program to start — the tool itself, or <c>cmd.exe</c>.</param>
/// <param name="Arguments">
/// The whole command line for that program, as <c>ProcessStartInfo.Arguments</c> takes it.
/// </param>
internal readonly record struct WincFlashCommand(string FileName, string Arguments);

/// <summary>
/// Builds the command line for the WINC flash tool. It starts no process.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure function so the exact string it produces can be asserted on any platform. The
/// defect it exists for only shows on Windows, where the tool is a <c>.cmd</c> launcher and the
/// line therefore goes through <c>cmd.exe</c> (issue #844, part of #271).
/// </para>
/// <para>
/// There are two quoting rules here, and they are not the same rule.
/// </para>
/// <para>
/// <b>Direct launch</b> (an <c>.exe</c> tool, or any tool off Windows). The line is parsed by the
/// target program with the usual Windows argument rules, which <c>CommandLineToArgvW</c> and
/// .NET both implement: a value is wrapped in quotes when it holds whitespace or a quote, an
/// embedded quote becomes <c>\"</c>, and the backslashes in front of a quote are doubled. Every
/// value can be encoded, so nothing is refused.
/// </para>
/// <para>
/// <b>Through <c>cmd.exe</c></b> (a <c>.cmd</c> or <c>.bat</c> tool on Windows). <c>cmd.exe</c>
/// parses the line first, and it keeps the quotes as written only when the line holds exactly
/// two of them. With more than two it strips the first and the last, which cuts the quotes off
/// the tool path and breaks the line. So the whole command goes inside one extra pair of quotes
/// and <c>/s</c> strips exactly that pair:
/// <c>cmd.exe /d /s /v:off /c ""&lt;tool&gt;" &lt;args&gt;"</c>. <c>/d</c> skips the AutoRun
/// commands in the registry and <c>/v:off</c> turns off delayed expansion, so a <c>!</c> in a
/// value stays a <c>!</c>.
/// </para>
/// <para>
/// Inside that pair, <c>cmd.exe</c> still reads <c>&amp;</c>, <c>|</c>, <c>&lt;</c>, <c>&gt;</c>,
/// <c>^</c>, <c>(</c>, <c>)</c>, <c>,</c>, <c>;</c> and <c>=</c> as syntax, and a quoted value
/// makes them data again. Two characters have no encoding at all, and a value that holds one is
/// refused rather than passed:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A double quote. <c>cmd.exe</c> counts quotes to find the quoted runs, so a quote inside a
/// value ends the run early and turns the rest of the value into command syntax. <c>^"</c> is
/// not an escape inside a quoted run.
/// </description></item>
/// <item><description>
/// A percent sign. <c>cmd.exe</c> expands <c>%NAME%</c> even inside quotes, and <c>%%</c> is an
/// escape only inside a batch file, not on a command line.
/// </description></item>
/// </list>
/// <para>
/// A control character (NUL, CR, LF) is refused under both rules, because no command line can
/// carry it.
/// </para>
/// <para>
/// Note that the <c>cmd.exe</c> rule quotes but does not escape backslashes. The tokens go to
/// <c>cmd.exe</c> and then to a batch script, which reads <c>%~1</c> by stripping the
/// surrounding quotes. It does not apply the backslash rules above, so a path that ends in a
/// backslash is correct as <c>"C:\"</c> here.
/// </para>
/// </remarks>
internal static class WincFlashCommandLine
{
    /// <summary>Characters that make <c>cmd.exe</c> read a value as syntax.</summary>
    private static readonly char[] CmdQuoteTriggers =
        [' ', '\t', '&', '|', '<', '>', '^', '(', ')', ',', ';', '='];

    /// <summary>Characters that make an argument need quotes under the Windows argument rules.</summary>
    private static readonly char[] ArgvQuoteTriggers = [' ', '\t', '"'];

    /// <summary>Characters no command line can carry.</summary>
    private static readonly char[] ControlCharacters = ['\0', '\r', '\n'];

    /// <summary>
    /// Builds the command that flashes the WINC module.
    /// </summary>
    /// <param name="toolPath">Full path of the flash tool.</param>
    /// <param name="argumentsTemplate">
    /// The argument template, with the <c>{port}</c> and <c>{firmwarePath}</c> placeholders. The
    /// template itself is the caller's own switches and is used as written; only the values that
    /// go into the placeholders are quoted.
    /// </param>
    /// <param name="port">The serial port for <c>{port}</c>.</param>
    /// <param name="firmwarePath">The firmware path for <c>{firmwarePath}</c>.</param>
    /// <param name="throughWindowsShell">
    /// <see langword="true"/> to run the tool through <c>cmd.exe</c>, which a <c>.cmd</c> or
    /// <c>.bat</c> tool on Windows needs.
    /// </param>
    /// <returns>The program to start and its whole command line.</returns>
    /// <exception cref="InvalidOperationException">
    /// A value holds a character that this command line cannot carry. The message says which
    /// value, which character, and what to do.
    /// </exception>
    internal static WincFlashCommand Build(
        string toolPath,
        string argumentsTemplate,
        string port,
        string firmwarePath,
        bool throughWindowsShell)
    {
        ArgumentNullException.ThrowIfNull(toolPath);
        ArgumentNullException.ThrowIfNull(argumentsTemplate);

        var quotedPort = QuoteValue(port, "serial port", throughWindowsShell);
        var quotedFirmwarePath = QuoteValue(firmwarePath, "firmware path", throughWindowsShell);

        var toolArguments = argumentsTemplate
            .Replace("{port}", quotedPort, StringComparison.Ordinal)
            .Replace("{firmwarePath}", quotedFirmwarePath, StringComparison.Ordinal);

        if (!throughWindowsShell)
        {
            // The tool path goes to ProcessStartInfo.FileName, which is a path and not a command
            // line, so it needs no quotes of its own.
            return new WincFlashCommand(toolPath, toolArguments);
        }

        RejectUnencodableForCmd(toolPath, "flash tool path");

        // The tool path is always quoted, so a space in it is safe, and /s strips the one outer
        // pair that holds the whole command.
        return new WincFlashCommand(
            "cmd.exe",
            $"/d /s /v:off /c \"\"{toolPath}\" {toolArguments}\"");
    }

    private static string QuoteValue(string? value, string role, bool throughWindowsShell)
    {
        var text = value ?? string.Empty;
        return throughWindowsShell ? QuoteForCmd(text, role) : QuoteForArgv(text, role);
    }

    /// <summary>
    /// Quotes a value for the line that <c>cmd.exe</c> parses.
    /// </summary>
    private static string QuoteForCmd(string value, string role)
    {
        RejectUnencodableForCmd(value, role);

        if (value.Length == 0)
        {
            return "\"\"";
        }

        // Quoted only when it has to be. A port such as COM7 goes through bare, which is what a
        // batch script that reads %1 rather than %~1 expects.
        return value.IndexOfAny(CmdQuoteTriggers) >= 0 ? $"\"{value}\"" : value;
    }

    /// <summary>
    /// Quotes a value with the Windows argument rules, for a program started directly.
    /// </summary>
    private static string QuoteForArgv(string value, string role)
    {
        RejectControlCharacters(value, role);

        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.IndexOfAny(ArgvQuoteTriggers) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');

        var backslashes = 0;
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    backslashes++;
                    break;

                case '"':
                    // Every backslash in front of a quote is doubled, and the quote itself gets
                    // one more, so the parser reads the backslashes as data and the quote as a
                    // quote.
                    builder.Append('\\', (backslashes * 2) + 1);
                    backslashes = 0;
                    builder.Append('"');
                    break;

                default:
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                    builder.Append(character);
                    break;
            }
        }

        // The run of backslashes at the end sits in front of the closing quote, so it is doubled
        // too. Otherwise it would escape that quote and swallow the end of the argument.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void RejectUnencodableForCmd(string value, string role)
    {
        RejectControlCharacters(value, role);

        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw Reject(
                value,
                role,
                "a double quote",
                "cmd.exe counts quotes to find the quoted parts of a command line, so a quote "
                + "inside a value ends the quoted part early and turns the rest of the value into "
                + "commands. There is no escape for it");
        }

        if (value.Contains('%', StringComparison.Ordinal))
        {
            throw Reject(
                value,
                role,
                "a percent sign",
                "cmd.exe replaces %NAME% with an environment variable even inside quotes, so the "
                + "value cannot be passed as written. %% is an escape only inside a batch file");
        }
    }

    private static void RejectControlCharacters(string value, string role)
    {
        if (value.IndexOfAny(ControlCharacters) >= 0)
        {
            throw Reject(
                value,
                role,
                "a control character",
                "a command line cannot carry a null, a carriage return or a line feed");
        }
    }

    private static InvalidOperationException Reject(
        string value,
        string role,
        string character,
        string reason) =>
        new($"The WiFi flash command line cannot carry the {role} '{value}': it holds "
            + $"{character}, and {reason}. Remove that character from the {role}, or give a "
            + $"different {role}. The value is refused instead of changed, because a changed "
            + "command line can run the wrong command.");
}
