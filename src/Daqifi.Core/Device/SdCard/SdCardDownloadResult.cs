using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Result of an SD card file download operation.
/// </summary>
public sealed record SdCardDownloadResult
{
    /// <summary>
    /// Gets the name of the downloaded file.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets the size of the downloaded file in bytes.
    /// </summary>
    public required long SizeInBytes { get; init; }

    /// <summary>
    /// Gets how long the download took.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the local file path, if the file was downloaded to disk.
    /// </summary>
    public string? FilePath { get; init; }
}
