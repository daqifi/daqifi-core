using System;

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

        private static bool MatchesErrorPrefix(string trimmed, string prefix)
        {
            if (trimmed.Length < prefix.Length
                || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (trimmed.Length == prefix.Length)
                return true;
            var next = trimmed[prefix.Length];
            if (next == ':' || next == ' ' || next == '\t')
                return true;
            // Single '!' is ambiguous (could be a filename like "error!log.bin").
            // Require '!!' so we still catch firmware "Error!!" status text but
            // let plain filenames pass through.
            return next == '!'
                && trimmed.Length > prefix.Length + 1
                && trimmed[prefix.Length + 1] == '!';
        }
    }
}
