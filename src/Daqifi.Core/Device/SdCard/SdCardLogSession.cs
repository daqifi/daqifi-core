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
    /// <remarks>
    /// Nothing is decoded until this is enumerated, and the log is re-read from the start on
    /// each enumeration, so the source must still be readable: a session parsed from a path
    /// needs the file to still exist, and a session parsed from a stream needs that stream
    /// still open and left alone. Enumerate a stream-backed session's samples one at a time —
    /// a stream has a single read position, so a second, overlapping enumeration throws
    /// rather than silently interleaving reads.
    /// </remarks>
    public IAsyncEnumerable<SdCardLogEntry> Samples { get; }

    /// <summary>
    /// Gets the timestamp clock frequency in Hz that was actually used to convert this log's
    /// raw tick counts into times, or <c>0</c> when no conversion was possible.
    /// </summary>
    /// <remarks>
    /// Read this together with <see cref="TimestampFrequencySource"/>. A frequency that does
    /// not match the recording device rescales every timestamp in the session by a constant
    /// factor, and nothing in the sample data reveals it.
    /// </remarks>
    public uint TimestampFrequency { get; init; }

    /// <summary>
    /// Gets where <see cref="TimestampFrequency"/> came from.
    /// </summary>
    /// <remarks>
    /// <see cref="SdCardTimestampSource.Fallback"/> means the frequency was a guess supplied by
    /// the caller rather than a figure reported by the file or the device, so the timestamps
    /// should not be trusted as absolute elapsed time.
    /// </remarks>
    public SdCardTimestampSource TimestampFrequencySource { get; init; }

    /// <summary>
    /// Gets how many analog values an entry of this log was seen to carry, or <c>0</c> when no
    /// entry carrying values was seen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the width of the data, not a figure the file states. It is what
    /// <see cref="Daqifi.Core.Logging.Export.SdCardLogSessionExtensions.AsSampleSource"/> reads
    /// the export's column count from when the log carries no configuration block to state
    /// <see cref="SdCardDeviceConfiguration.AnalogPortCount"/>. A protobuf log normally carries
    /// none, and taking the column count as zero there discarded every analog reading in the
    /// file while reporting a successful export (issue #902).
    /// </para>
    /// <para>
    /// Only the protobuf path has to measure this. A CSV or JSON log names its columns in its
    /// own header, so <see cref="DeviceConfig"/> already states the width and this stays
    /// <c>0</c>.
    /// </para>
    /// <para>
    /// It is the widest entry seen while the configuration scan ran, so it is bounded by
    /// <see cref="SdCardParseOptions.ConfigurationScanMessageLimit"/> rather than by the file's
    /// length. A log whose first values appear beyond that window still exports short, and
    /// <see cref="Daqifi.Core.Logging.Export.SdCardLogSampleSource.DroppedAnalogColumns"/> is
    /// what reports it.
    /// </para>
    /// </remarks>
    public int ObservedAnalogValueCount { get; init; }

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
