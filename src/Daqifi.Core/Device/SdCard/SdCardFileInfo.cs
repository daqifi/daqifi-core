using System;

#nullable enable

namespace Daqifi.Core.Device.SdCard;

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
    /// Gets the file size in bytes as reported by the device's directory listing, or
    /// <c>null</c> when the listing entry carried no size. Firmware emits
    /// <c>"&lt;path&gt; &lt;size&gt;"</c> per entry; a listing produced without a size token
    /// leaves this unset rather than guessing zero, because a real 0-byte size is meaningful
    /// (it is what distinguishes a legitimately empty file from a wedged SD subsystem during a
    /// download — see <see cref="SdCardEmptyTransferException"/>).
    /// </summary>
    public long? SizeInBytes { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileInfo"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="createdDate">The date the file was created, if known.</param>
    /// <param name="sizeInBytes">The size in bytes reported by the directory listing, if known.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sizeInBytes"/> is negative.
    /// </exception>
    public SdCardFileInfo(string fileName, DateTime? createdDate = null, long? sizeInBytes = null)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));

        if (sizeInBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeInBytes), sizeInBytes, "File size cannot be negative.");
        }

        CreatedDate = createdDate;
        SizeInBytes = sizeInBytes;
    }
}
