using System;
using System.Collections.Generic;

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
    public class SdCardEmptyTransferException : SdCardOperationException
    {
        /// <summary>
        /// Gets the name of the file that produced the empty transfer.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardEmptyTransferException"/> class.
        /// </summary>
        /// <param name="fileName">The name of the file that produced the empty transfer.</param>
        public SdCardEmptyTransferException(string fileName)
            : base(
                $"Received an empty (marker-only) transfer for SD card file '{fileName}'. " +
                "The device's SD subsystem may not be ready; retry or power-cycle the device.",
                Array.Empty<string>())
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        }
    }
}
