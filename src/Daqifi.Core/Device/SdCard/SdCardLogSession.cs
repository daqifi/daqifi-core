using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Represents a complete parsed SD card log file.
/// </summary>
public sealed class SdCardLogSession
{
    /// <summary>
    /// Gets the source file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the file creation date, parsed from the filename pattern <c>log_YYYYMMDD_HHMMSS.bin</c>
    /// or provided via parse options.
    /// </summary>
    public DateTime? FileCreatedDate { get; }

    /// <summary>
    /// Gets the device configuration extracted from the first status message, if present.
    /// </summary>
    public SdCardDeviceConfiguration? DeviceConfig { get; }

    /// <summary>
    /// Gets streaming access to sample data. Samples are produced lazily as the file is read.
    /// </summary>
    public IAsyncEnumerable<SdCardLogEntry> Samples { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardLogSession"/> class.
    /// </summary>
    /// <param name="fileName">The source file name.</param>
    /// <param name="fileCreatedDate">The file creation date.</param>
    /// <param name="deviceConfig">Device configuration from status message.</param>
    /// <param name="samples">Async enumerable of parsed sample entries.</param>
    public SdCardLogSession(
        string fileName,
        DateTime? fileCreatedDate,
        SdCardDeviceConfiguration? deviceConfig,
        IAsyncEnumerable<SdCardLogEntry> samples)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        FileCreatedDate = fileCreatedDate;
        DeviceConfig = deviceConfig;
    }
}
