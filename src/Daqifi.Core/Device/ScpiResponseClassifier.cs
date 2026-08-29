using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Shared classification for SCPI text response lines. Used by both the
/// general streaming-device response handling and SD card listing parsing
/// so the error/status line rule stays consistent across call sites.
/// </summary>
internal static class ScpiResponseClassifier
{
    /// <summary>
    /// Returns true if the line is a non-result error/status line that
    /// response parsers should drop. Matches both SCPI error responses
    /// (canonical <c>**ERROR</c> marker, bare <c>ERROR</c> token followed
    /// by a SCPI delimiter <c>:</c> / space / tab / end-of-line) and
    /// firmware status text (<c>Error !! ...</c> with space, or the
    /// no-space <c>Error!!</c> form). A double-<c>!</c> is required when
    /// no other delimiter is present so legitimate filenames like
    /// <c>error!log.bin</c> aren't dropped — single <c>!</c> alone is
    /// ambiguous between error-status and filename. Trims both ends so
    /// a bare <c>"ERROR\r"</c> from CRLF line endings still classifies.
    /// Plain filenames whose basename starts with <c>error</c> /
    /// <c>Errors</c> pass through unmatched (closes #190).
    /// </summary>
    internal static bool IsErrorResponseLine(string line)
    {
        var trimmed = line.Trim();
        return MatchesErrorPrefix(trimmed, "**ERROR")
               || MatchesErrorPrefix(trimmed, "ERROR");
    }

    /// <summary>
    /// Returns true if the line is a genuine SCPI-formatted error line: the canonical
    /// <c>**ERROR</c> marker, or a bare <c>ERROR</c> token, in either case followed by
    /// <c>:</c>, end-of-line, or a space/tab that in turn precedes an error code (a digit
    /// or <c>-</c>) — e.g. <c>**ERROR: -200,...</c>, <c>ERROR -200,...</c>, or
    /// <c>ERROR\t-200,...</c>. Unlike <see cref="IsErrorResponseLine"/>, this deliberately
    /// excludes the firmware <c>Error !! ...</c> status-text form (space followed by
    /// non-numeric text), so callers that need to surface only a real SCPI error (e.g. as a
    /// typed exception's error-code field) don't pick up non-SCPI status text. Trims both
    /// ends so a bare <c>"ERROR\r"</c> from CRLF line endings still classifies.
    /// </summary>
    internal static bool IsScpiErrorLine(string line)
    {
        var trimmed = line.Trim();
        return MatchesStrictScpiErrorPrefix(trimmed, "**ERROR")
               || MatchesStrictScpiErrorPrefix(trimmed, "ERROR");
    }

    /// <summary>
    /// Returns true when any line in the response is a genuine SCPI error line. These can
    /// appear transiently — e.g. while the firmware is still switching the shared SPI bus —
    /// which is why several callers retry on this rather than failing outright.
    /// </summary>
    internal static bool ContainsScpiError(IReadOnlyList<string> lines)
    {
        return lines.Any(IsScpiErrorLine);
    }

    /// <summary>
    /// Returns the last genuine SCPI-formatted error line in the response, trimmed, or
    /// <c>null</c> if none is present. Several callers translate a device response into a
    /// typed exception and need exactly this line — the most recent thing the device said
    /// that is shaped like a real SCPI error, per <see cref="IsScpiErrorLine"/> — so this
    /// centralizes the "get it, trimmed" step they all otherwise repeated identically.
    /// </summary>
    internal static string? GetLastScpiErrorLine(IReadOnlyList<string> lines)
    {
        return lines.LastOrDefault(IsScpiErrorLine)?.Trim();
    }

    /// <summary>
    /// Returns true when the response contains at least one non-empty line and every non-empty
    /// line is an error/status line per <see cref="IsErrorResponseLine"/> — i.e. the device
    /// answered, and had nothing but a complaint to say. A response with no lines at all is not
    /// error-only, so callers can still tell "the log is empty" from "the command failed".
    /// </summary>
    internal static bool IsErrorOnlyResponse(IReadOnlyList<string> lines)
    {
        var sawContent = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            sawContent = true;
            if (!IsErrorResponseLine(line))
            {
                return false;
            }
        }

        return sawContent;
    }

    /// <summary>
    /// Returns true when a line carries characters a SCPI text reply cannot contain: a control
    /// character other than tab, or the U+FFFD replacement character that UTF-8 decoding
    /// produces for a byte sequence that is not text at all. Either one means non-text bytes
    /// were interleaved with the reply — on this device, protobuf frames from a stream the
    /// firmware never stopped emitting (issue #537).
    /// </summary>
    /// <remarks>
    /// Tab is excluded because the firmware does use it as a delimiter — <see cref="TokenDelimiters"/>
    /// accepts it in real device output. CR and LF are the line separators, so they are already
    /// gone by the time a line reaches here.
    /// <para>
    /// This detects evidence, not state: it can only fire on a line that actually arrived
    /// mangled, so a reply taken while the device is idle can never trip it. The converse does
    /// not hold — non-text bytes that happen to decode to printable characters are
    /// indistinguishable from real content and pass through, which is why callers treat a
    /// negative answer as "no evidence of corruption" rather than "known clean".
    /// </para>
    /// </remarks>
    internal static bool IsBinaryCorruptedLine(string? line)
    {
        if (line == null)
        {
            return false;
        }

        foreach (var c in line)
        {
            if (c == '\t')
            {
                continue;
            }

            if (char.IsControl(c) || c == UnicodeReplacementChar)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when any line in the response is binary-corrupted per
    /// <see cref="IsBinaryCorruptedLine"/>.
    /// </summary>
    internal static bool ContainsBinaryCorruptedLine(IReadOnlyList<string> lines)
    {
        return lines.Any(IsBinaryCorruptedLine);
    }

    /// <summary>
    /// The character <see cref="System.Text.Encoding.UTF8"/> substitutes for a byte sequence it
    /// cannot decode. The text consumer decodes response bytes as UTF-8, so raw binary reaches
    /// the parsers as a mix of these and control characters.
    /// </summary>
    private const char UnicodeReplacementChar = '\uFFFD';

    /// <summary>
    /// Returns true if the line is the reply to a <c>SYSTem:ERRor?</c> query — the IEEE 488.2
    /// error-queue format <c>&lt;code&gt;,"&lt;message&gt;"</c>, e.g. <c>0,"No error"</c> or
    /// <c>-200,"Execution error"</c>. Deliberately narrow: the code must be the first thing on
    /// the line and the quoted message must run to the end of it, so a device-emitted SD listing
    /// entry (always <c>&lt;path&gt; &lt;size&gt;</c>, space-separated, per the firmware's
    /// <c>"%s %u\r\n"</c> format) can never match. Unlike <see cref="IsScpiErrorLine"/> this does
    /// not require an <c>ERROR</c> token — the query reply carries the bare code.
    /// </summary>
    /// <remarks>
    /// Used as the end-of-response marker for the SD card directory listing (closes #396): the
    /// firmware emits no terminator of its own and writes nothing at all for an empty directory,
    /// so Core appends a <c>SYSTem:ERRor?</c> query to the same text exchange and treats its
    /// reply as proof that everything the device had to say about the listing has arrived.
    /// </remarks>
    internal static bool IsSystemErrorReplyLine(string line)
    {
        var trimmed = line.Trim();

        var index = 0;
        if (index < trimmed.Length && (trimmed[index] == '+' || trimmed[index] == '-'))
        {
            index++;
        }

        var digitStart = index;
        while (index < trimmed.Length && trimmed[index] >= '0' && trimmed[index] <= '9')
        {
            index++;
        }

        if (index == digitStart)
        {
            return false;
        }

        index = SkipSpaces(trimmed, index);

        if (index >= trimmed.Length || trimmed[index] != ',')
        {
            return false;
        }

        index = SkipSpaces(trimmed, index + 1);

        // Require an opening quote and a distinct closing quote at end-of-line.
        return index < trimmed.Length - 1
               && trimmed[index] == '"'
               && trimmed[trimmed.Length - 1] == '"';
    }

    /// <summary>
    /// Parses the numeric code out of a <c>SYSTem:ERRor?</c> reply — <c>0</c> from
    /// <c>0,"No error"</c>, <c>-200</c> from <c>-200,"Execution error"</c>. Pair it with
    /// <see cref="IsSystemErrorReplyLine"/>, which decides whether a line is such a reply at all;
    /// this only reads the code out of one that is.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TryExtractErrorCode"/>, which parses the <c>**ERROR: -200,...</c>
    /// form the device volunteers alongside a command's output: that one requires the <c>ERROR</c>
    /// token, and the error-queue reply carries the bare code.
    /// </remarks>
    /// <param name="line">The candidate <c>SYSTem:ERRor?</c> reply line.</param>
    /// <param name="code">The parsed code when the method returns <c>true</c>; otherwise 0.</param>
    /// <returns><c>true</c> if a numeric code was read; otherwise <c>false</c>.</returns>
    internal static bool TryParseSystemErrorReplyCode(string line, out int code)
    {
        return TryParseLeadingCode(line, out code);
    }

    /// <summary>
    /// Reads the numeric code that prefixes a SCPI error payload: the text up to the first
    /// comma (or the whole of it when there is no comma), trimmed, parsed as a culture-invariant
    /// integer. Single-sourced here because both error forms the device produces end in this same
    /// step — the bare <c>-200,"Execution error"</c> of a <c>SYSTem:ERRor?</c> reply and the
    /// <c>**ERROR: -200,"Execution error"</c> the device volunteers — and they must agree on
    /// what counts as a code.
    /// </summary>
    /// <param name="text">The error payload, with any leading <c>ERROR</c> token already stripped.</param>
    /// <param name="code">The parsed code when the method returns <c>true</c>; otherwise 0.</param>
    /// <returns><c>true</c> if a numeric code was read; otherwise <c>false</c>.</returns>
    private static bool TryParseLeadingCode(ReadOnlySpan<char> text, out int code)
    {
        var trimmed = text.Trim();
        var commaIndex = trimmed.IndexOf(',');
        var codeSpan = (commaIndex >= 0 ? trimmed[..commaIndex] : trimmed).Trim();
        return int.TryParse(codeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    private static int SkipSpaces(string value, int index)
    {
        while (index < value.Length && (value[index] == ' ' || value[index] == '\t'))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Extracts the numeric error code from a SCPI error line — e.g. <c>-200</c> from
    /// <c>**ERROR: -200,"Execution error"</c>, <c>ERROR -113,"Undefined header"</c>, or
    /// <c>**ERROR\t-113,...</c>. The delimiter between the <c>ERROR</c>/<c>**ERROR</c> token and
    /// the code may be <c>:</c>, space, or tab — the same set the line matchers above accept, kept
    /// here so the accepted delimiters can't drift between detection and extraction. The code is
    /// the text up to the following comma (if any).
    /// </summary>
    /// <param name="line">The candidate error line.</param>
    /// <param name="code">The parsed error code when the method returns <c>true</c>; otherwise 0.</param>
    /// <returns><c>true</c> if a numeric error code was extracted; otherwise <c>false</c>.</returns>
    internal static bool TryExtractErrorCode(string line, out int code)
    {
        code = 0;
        var trimmed = line.TrimStart();

        string afterToken;
        if (trimmed.StartsWith("**ERROR", StringComparison.OrdinalIgnoreCase))
        {
            afterToken = trimmed[7..];
        }
        else if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            afterToken = trimmed[5..];
        }
        else
        {
            return false;
        }

        afterToken = afterToken.TrimStart(TokenDelimiters);

        return TryParseLeadingCode(afterToken, out code);
    }

    /// <summary>
    /// The delimiters accepted between the <c>ERROR</c>/<c>**ERROR</c> token and the error code.
    /// Single-sourced here: the line matchers test membership via <see cref="IsTokenDelimiter"/>, and
    /// <see cref="TryExtractErrorCode"/> trims with this array directly — both draw from this one set,
    /// so detection and extraction can't drift.
    /// </summary>
    private static readonly char[] TokenDelimiters = { ':', ' ', '\t' };

    /// <summary>Returns true if <paramref name="c"/> is one of the accepted <see cref="TokenDelimiters"/>.</summary>
    private static bool IsTokenDelimiter(char c) => Array.IndexOf(TokenDelimiters, c) >= 0;

    private static bool MatchesErrorPrefix(string trimmed, string prefix)
    {
        if (trimmed.Length < prefix.Length
            || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Length == prefix.Length)
            return true;
        var next = trimmed[prefix.Length];
        if (IsTokenDelimiter(next))
            return true;
        // Single '!' is ambiguous (could be a filename like "error!log.bin").
        // Require '!!' so we still catch firmware "Error!!" status text but
        // let plain filenames pass through.
        return next == '!'
            && trimmed.Length > prefix.Length + 1
            && trimmed[prefix.Length + 1] == '!';
    }

    private static bool MatchesStrictScpiErrorPrefix(string trimmed, string prefix)
    {
        if (trimmed.Length < prefix.Length
            || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Length == prefix.Length)
            return true;

        var next = trimmed[prefix.Length];
        if (next == ':')
            return true;
        // ':' already returned above, so this rejects anything that isn't a space/tab delimiter.
        if (!IsTokenDelimiter(next))
            return false;

        // A space/tab delimiter alone is ambiguous — firmware status text like
        // "Error !! No SD Card Detected" also uses a space after "Error". Only
        // treat it as a real SCPI error when what follows looks like an error
        // code (digit or leading '-').
        var rest = trimmed[(prefix.Length + 1)..].TrimStart(' ', '\t');
        return rest.Length > 0 && (char.IsDigit(rest[0]) || rest[0] == '-');
    }
}
