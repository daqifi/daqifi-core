using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Parses SD card file listing responses from the device into <see cref="SdCardFileInfo"/> objects.
    /// </summary>
    public static class SdCardFileListParser
    {
        private const string DateFormat = "yyyyMMdd_HHmmss";
        private const string LogFilePrefix = "log_";
        private const string DaqifiDirectoryPrefix = "Daqifi/";

        /// <summary>
        /// Parses a collection of text lines from the SD card file list response into file info objects.
        /// </summary>
        /// <param name="lines">The raw text lines from the device response.</param>
        /// <returns>A list of parsed file information objects.</returns>
        public static IReadOnlyList<SdCardFileInfo> ParseFileList(IEnumerable<string> lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            var files = new List<SdCardFileInfo>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var path = line.Trim();

                // Skip SCPI error responses: "**ERROR: -200, ..." or
                // "ERROR: -200, ...". Bare "ERROR" prefix can't be used
                // here because filenames like "error_log.csv" /
                // "Errors_summary.bin" emitted without the Daqifi/
                // directory prefix would also match (closes #190 second
                // location — IsNonResultLine in DaqifiStreamingDevice
                // had the same bug). Match `ERROR` only when followed
                // by an SCPI delimiter (':', ' ', '!', '\t') or end of
                // line so ordinary filenames pass through.
                if (IsErrorResponseLine(path))
                {
                    continue;
                }

                // If a file size is present after the path, keep only the first token.
                var tokenEnd = path.IndexOfAny(new[] { ' ', '\t' });
                if (tokenEnd > 0)
                {
                    path = path.Substring(0, tokenEnd);
                }

                // Strip "Daqifi/" directory prefix if present
                if (path.StartsWith(DaqifiDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(DaqifiDirectoryPrefix.Length);
                }

                // Extract just the filename from the path
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                // Skip filenames with control characters (corrupted SD card directory entries)
                if (fileName.Any(char.IsControl))
                {
                    continue;
                }

                var createdDate = TryParseDateFromLogFileName(fileName);
                files.Add(new SdCardFileInfo(fileName, createdDate));
            }

            return files;
        }

        /// <summary>
        /// Attempts to parse a date from a log filename with the pattern "log_YYYYMMDD_HHMMSS.bin".
        /// </summary>
        /// <param name="fileName">The filename to parse.</param>
        /// <returns>The parsed date, or null if the filename does not match the expected pattern.</returns>
        internal static DateTime? TryParseDateFromLogFileName(string fileName)
        {
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            if (!nameWithoutExtension.StartsWith(LogFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var dateString = nameWithoutExtension.Substring(LogFilePrefix.Length);

            if (DateTime.TryParseExact(
                    dateString,
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                return date;
            }

            return null;
        }

        /// <summary>
        /// Returns true if the line is a SCPI error response — either the
        /// canonical <c>**ERROR</c> marker or a bare <c>ERROR</c> token, in
        /// each case followed by a SCPI delimiter (<c>:</c>, space, <c>!</c>,
        /// tab) or end of line. Trims both ends so a bare <c>"ERROR\r"</c>
        /// from CRLF line endings still classifies. Plain filenames whose
        /// basename starts with <c>error</c> / <c>Errors</c> pass through
        /// unmatched (closes #190).
        /// </summary>
        /// <remarks>
        /// Shared with <c>DaqifiStreamingDevice.IsNonResultLine</c> so any
        /// future delimiter / trim refinement stays consistent across both
        /// SD-response classification paths.
        /// </remarks>
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
            return next == ':' || next == ' ' || next == '!' || next == '\t';
        }
    }
}
