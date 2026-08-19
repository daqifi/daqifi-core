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
        /// Marker the device appends to a directory listing so a host can tell a
        /// finished reply from a truncated one (firmware #794). The status word
        /// follows it: <c>OK</c>, <c>INCOMPLETE</c> or <c>FAILED</c>.
        /// </summary>
        internal const string ListEndMarker = "__END_OF_LIST__";

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

                // Skip SCPI error responses ("**ERROR: -200, ...", "ERROR: -200, ...")
                // and firmware status text ("Error !! ..."). Classification rule lives
                // in ScpiResponseClassifier so it stays consistent with
                // DaqifiStreamingDevice.IsNonResultLine (closes #190).
                if (ScpiResponseClassifier.IsErrorResponseLine(path))
                {
                    continue;
                }

                // The end-of-listing marker is framing, not a file. Without this
                // it survives every filter below -- it is not blank, not an error
                // shape, and its first token is not empty -- and the device would
                // appear to be carrying a file called "__END_OF_LIST__".
                if (IsListEndMarker(path, out _))
                {
                    continue;
                }

                // Firmware emits "<path> <size>" per entry. Keep the path as the first token and
                // retain the size — it is what lets a download tell a legitimately empty file from
                // a wedged SD subsystem serving nothing (see SdCardEmptyTransferException).
                long? sizeInBytes = null;
                var tokenEnd = path.IndexOfAny(new[] { ' ', '\t' });
                if (tokenEnd > 0)
                {
                    sizeInBytes = TryParseSize(path.Substring(tokenEnd + 1));
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
                files.Add(new SdCardFileInfo(fileName, createdDate, sizeInBytes));
            }

            return files;
        }

        /// <summary>
        /// Parses the size token that follows a listing entry's path. Anything that is not a
        /// plain non-negative integer (extra columns, a malformed line) yields <c>null</c> —
        /// "size unknown" — rather than a guessed value.
        /// </summary>
        private static long? TryParseSize(string sizeToken)
        {
            var trimmed = sizeToken.Trim();

            if (trimmed.Length == 0)
            {
                return null;
            }

            return long.TryParse(
                       trimmed,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var size)
                ? size
                : null;
        }

        /// <summary>
        /// True when <paramref name="line"/> IS the end-of-listing marker, as
        /// opposed to merely starting with its text.
        /// </summary>
        /// <remarks>
        /// The marker is either the token alone or the token followed by
        /// whitespace and a status word. A prefix match would swallow a file
        /// legitimately named <c>__END_OF_LIST__notes.csv</c> -- unlikely, but the
        /// cost of being exact is one comparison and the cost of being loose is a
        /// file the user cannot see.
        /// </remarks>
        /// <param name="line">The raw line, trimmed internally.</param>
        /// <param name="statusWord">The status word, or empty when there is none.</param>
        /// <returns>True when the line is the marker.</returns>
        private static bool IsListEndMarker(string line, out string statusWord)
        {
            statusWord = string.Empty;

            var trimmed = line.Trim();
            if (!trimmed.StartsWith(ListEndMarker, StringComparison.Ordinal))
            {
                return false;
            }

            if (trimmed.Length == ListEndMarker.Length)
            {
                return true;
            }

            if (!char.IsWhiteSpace(trimmed[ListEndMarker.Length]))
            {
                return false;
            }

            statusWord = trimmed.Substring(ListEndMarker.Length).Trim();
            return true;
        }

        /// <summary>
        /// How a directory listing ended, read from the device's end-of-listing
        /// marker (firmware #794).
        /// </summary>
        /// <remarks>
        /// Before that firmware a listing simply stopped, so a complete reply and
        /// one cut short by a timeout, a buffer boundary or a stall-abort were
        /// byte-identical. <see cref="SdCardListingStatus.Unterminated"/> is that
        /// case and is NOT an error: it means the device cannot say, so a caller
        /// must not report absence as fact.
        /// </remarks>
        /// <param name="lines">The raw text lines from the device response.</param>
        /// <returns>The listing's terminating status.</returns>
        public static SdCardListingStatus GetListingStatus(IEnumerable<string> lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            // LAST marker wins. A reply that somehow carries two is already
            // suspect, and the one that ends the reply is the one describing the
            // walk that produced it.
            var status = SdCardListingStatus.Unterminated;
            foreach (var line in lines)
            {
                if (!IsListEndMarker(line ?? string.Empty, out var word))
                {
                    continue;
                }

                if (word.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    status = SdCardListingStatus.Complete;
                }
                else if (word.Equals("INCOMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    status = SdCardListingStatus.Incomplete;
                }
                else if (word.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    status = SdCardListingStatus.Failed;
                }
                else
                {
                    // A marker with a status word this version does not know is
                    // still a terminated listing, but its contents cannot be
                    // trusted as complete -- treat it the way an explicitly
                    // partial one is treated rather than as success.
                    status = SdCardListingStatus.Incomplete;
                }
            }

            return status;
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
    }
}
