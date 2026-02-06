using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Result of an SD card file download operation.
/// </summary>
/// <param name="FileName">The name of the downloaded file.</param>
/// <param name="FileSize">The size of the downloaded file in bytes.</param>
/// <param name="Duration">How long the download took.</param>
/// <param name="FilePath">The local file path, if the file was downloaded to disk.</param>
public sealed record SdCardDownloadResult(
    string FileName,
    long FileSize,
    TimeSpan Duration,
    string? FilePath = null);
