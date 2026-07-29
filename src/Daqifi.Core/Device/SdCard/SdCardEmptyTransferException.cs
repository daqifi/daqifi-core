using System;
using System.Globalization;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Thrown when an SD card file download completes with only the <c>__END_OF_FILE__</c>
    /// marker and zero content bytes. A device whose SD subsystem is wedged or not yet ready
    /// can open the requested file successfully but return no data before closing it — this
    /// is never a valid download for a file the directory listing reports as non-empty, and
    /// must not be mistaken for a legitimate empty file.
    /// </summary>
    /// <remarks>
    /// The directory listing's reported size is the only thing that separates a wedged SD
    /// subsystem from a genuinely 0-byte file (routinely left on a FAT card by an interrupted
    /// logging session) — both look identical on the wire. When the listed size is known,
    /// <see cref="SdCardFileReceiver"/> only throws this for a file the listing reported as
    /// non-empty and returns a legitimate 0-byte download otherwise. When it is unknown
    /// (<see cref="ListedSizeInBytes"/> is <c>null</c>), the receiver keeps the conservative
    /// behavior and throws, so a wedged subsystem is still caught and retried.
    /// </remarks>
    public class SdCardEmptyTransferException : SdCardOperationException
    {
        /// <summary>
        /// Gets the name of the file that produced the empty transfer.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Gets the size the directory listing reported for the file, or <c>null</c> when no
        /// listed size was available at the point the transfer was classified. A non-null value
        /// is always greater than zero: a listed 0-byte file is a legitimate empty download and
        /// does not raise this exception.
        /// </summary>
        public long? ListedSizeInBytes { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardEmptyTransferException"/> class.
        /// </summary>
        /// <param name="fileName">The name of the file that produced the empty transfer.</param>
        /// <param name="listedSizeInBytes">
        /// The size the directory listing reported for the file, or <c>null</c> if unknown.
        /// </param>
        public SdCardEmptyTransferException(string fileName, long? listedSizeInBytes = null)
            : base(
                BuildMessage(fileName, listedSizeInBytes),
                Array.Empty<string>())
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            ListedSizeInBytes = listedSizeInBytes;
        }

        private static string BuildMessage(string fileName, long? listedSizeInBytes)
        {
            var prefix = $"Received an empty (marker-only) transfer for SD card file '{fileName}'. ";

            if (listedSizeInBytes is { } listedSize)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}The directory listing reports it as {1} bytes, so the device's SD subsystem is " +
                    "not serving the file; retry or power-cycle the device.",
                    prefix,
                    listedSize);
            }

            return prefix + "The device's SD subsystem may not be ready; retry or power-cycle the device.";
        }
    }
}
