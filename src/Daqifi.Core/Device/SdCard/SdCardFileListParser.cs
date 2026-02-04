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

                var createdDate = TryParseDateFromFileName(fileName);
                files.Add(new SdCardFileInfo(fileName, createdDate));
            }

            return files;
        }

        /// <summary>
        /// Attempts to parse a date from a log filename with the pattern "log_YYYYMMDD_HHMMSS.bin".
        /// </summary>
        /// <param name="fileName">The filename to parse.</param>
        /// <returns>The parsed date, or null if the filename does not match the expected pattern.</returns>
        private static DateTime? TryParseDateFromFileName(string fileName)
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
