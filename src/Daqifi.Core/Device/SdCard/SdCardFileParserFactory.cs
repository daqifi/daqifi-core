using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Factory for creating SD card log file parsers based on file format.
/// Supports auto-detection via file extension (.bin, .json, .csv).
/// </summary>
public static class SdCardFileParserFactory
{
    /// <summary>
    /// Parses an SD card log file with format auto-detection from file extension.
    /// </summary>
    /// <param name="filePath">The path to the log file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public static async Task<SdCardLogSession> ParseFileAsync(
        string filePath,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var format = DetectFormat(filePath);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: options?.BufferSize ?? 64 * 1024,
            useAsync: true);

        return await ParseWithFormatAsync(stream, Path.GetFileName(filePath), format, options, ct);
    }

    /// <summary>
    /// Parses an SD card log file from a stream with format auto-detection from file name.
    /// </summary>
    /// <param name="fileStream">A readable stream containing the log data.</param>
    /// <param name="fileName">The file name (used for format detection and metadata).</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public static async Task<SdCardLogSession> ParseAsync(
        Stream fileStream,
        string fileName,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        ArgumentNullException.ThrowIfNull(fileName);

        var format = DetectFormat(fileName);
        return await ParseWithFormatAsync(fileStream, fileName, format, options, ct);
    }

    /// <summary>
    /// Parses an SD card log file using an explicitly specified format.
    /// </summary>
    /// <param name="fileStream">A readable stream containing the log data.</param>
    /// <param name="fileName">The file name (used for metadata and date extraction).</param>
    /// <param name="format">The log file format.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public static Task<SdCardLogSession> ParseWithFormatAsync(
        Stream fileStream,
        string fileName,
        SdCardLogFormat format,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        return format switch
        {
            SdCardLogFormat.Protobuf => new SdCardFileParser().ParseAsync(fileStream, fileName, options, ct),
            SdCardLogFormat.Json => new SdCardJsonFileParser().ParseAsync(fileStream, fileName, options, ct),
            SdCardLogFormat.Csv => new SdCardCsvFileParser().ParseAsync(fileStream, fileName, options, ct),
            _ => throw new ArgumentException($"Unsupported format: {format}", nameof(format))
        };
    }

    /// <summary>
    /// Detects the SD card log file format from the file extension.
    /// </summary>
    /// <param name="fileName">The file name or path.</param>
    /// <returns>The detected <see cref="SdCardLogFormat"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the file extension is not supported.</exception>
    public static SdCardLogFormat DetectFormat(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".bin" => SdCardLogFormat.Protobuf,
            ".json" => SdCardLogFormat.Json,
            ".csv" => SdCardLogFormat.Csv,
            var ext => throw new ArgumentException($"Unsupported file extension: {ext}. Supported extensions are .bin, .json, .csv", nameof(fileName))
        };
    }
}
