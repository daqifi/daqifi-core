using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Options for controlling SD card log file parsing.
/// </summary>
public sealed class SdCardParseOptions
{
    /// <summary>
    /// Gets or sets the session start time override.
    /// When set, this is used as the timestamp anchor instead of the filename-derived date.
    /// </summary>
    public DateTime? SessionStartTime { get; set; }

    /// <summary>
    /// Gets or sets the read buffer size in bytes. Default is 64 KB.
    /// </summary>
    public int BufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets an optional progress reporter.
    /// </summary>
    public IProgress<SdCardParseProgress>? Progress { get; set; }

    /// <summary>
    /// Gets or sets a fallback timestamp frequency (in Hz) to use when no
    /// <c>TimestampFreq</c> field is found in the file's protobuf messages.
    /// <para>
    /// Device firmware may not include <c>TimestampFreq</c> in SD card log data.
    /// When this fallback is set and the file contains no timestamp frequency,
    /// it will be used to convert raw tick deltas to elapsed time.
    /// </para>
    /// <para>
    /// This value is only used as a fallback — if the file contains a valid
    /// <c>TimestampFreq</c>, it takes precedence.
    /// </para>
    /// <para>
    /// Defaults to 50 MHz (the Nyquist device clock frequency). Set to 0 to
    /// disable the fallback entirely.
    /// </para>
    /// </summary>
    public uint FallbackTimestampFrequency { get; set; } = 50_000_000;

    /// <summary>
    /// Gets or sets a device configuration override. When set, fields from this
    /// config fill in any gaps not found in the file itself.
    /// <para>
    /// This is useful when the device is connected during download — the device's
    /// live status provides calibration, resolution, and port range values that
    /// may not be embedded in the SD card log file.
    /// </para>
    /// </summary>
    public SdCardDeviceConfiguration? ConfigurationOverride { get; set; }
}
