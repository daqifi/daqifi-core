using System;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Represents information about a file stored on the device's SD card.
    /// </summary>
    public class SdCardFileInfo
    {
        /// <summary>
        /// Gets the name of the file.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Gets the date the file was created, parsed from the filename if available.
        /// </summary>
        public DateTime? CreatedDate { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardFileInfo"/> class.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <param name="createdDate">The date the file was created, if known.</param>
        public SdCardFileInfo(string fileName, DateTime? createdDate = null)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            CreatedDate = createdDate;
        }
    }
}
