using System;
using System.Globalization;

#nullable enable

namespace Daqifi.Core.Device
{
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

            var commaIndex = afterToken.IndexOf(',');
            var codeSpan = (commaIndex >= 0 ? afterToken[..commaIndex] : afterToken).Trim();
            return int.TryParse(codeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
        }

        /// <summary>
        /// The delimiters accepted between the <c>ERROR</c>/<c>**ERROR</c> token and the error code.
        /// Single-sourced here and consulted via <see cref="IsTokenDelimiter"/> by both
        /// <see cref="TryExtractErrorCode"/> and the line matchers, so detection and extraction can't drift.
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
}
