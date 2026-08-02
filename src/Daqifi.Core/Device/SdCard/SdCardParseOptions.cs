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
    /// This value is the last resort. A frequency stated by the file wins, and a frequency
    /// reported by a connected device through <see cref="ConfigurationOverride"/> comes next;
    /// this fallback applies only when neither is available.
    /// </para>
    /// <para>
    /// Defaults to 50 MHz. That figure is a historical default and does <b>not</b> match
    /// shipped Nyquist firmware, which reports a 42 MHz timestamp clock — converting 42 MHz
    /// ticks as though they were 50 MHz makes every reconstructed timestamp roughly 19% fast.
    /// Prefer supplying <see cref="ConfigurationOverride"/> from the connected device so this
    /// guess is never needed, and check
    /// <see cref="SdCardLogSession.TimestampFrequencySource"/> on the parsed session to see
    /// whether it was. Set to 0 to disable the fallback entirely, which leaves tick counts
    /// unconverted rather than converted with a guess.
    /// </para>
    /// </summary>
    public uint FallbackTimestampFrequency { get; set; } = 50_000_000;

    /// <summary>
    /// Gets or sets a device configuration override. When set, fields from this
    /// config fill in any gaps not found in the file itself.
    /// <para>
    /// This is useful when the device is connected during download — the device's
    /// live status provides calibration, resolution, port range, and timestamp clock
    /// values that may not be embedded in the SD card log file. Build one with
    /// <see cref="SdCardDeviceConfiguration.FromDevice"/>.
    /// </para>
    /// </summary>
    public SdCardDeviceConfiguration? ConfigurationOverride { get; set; }
}
