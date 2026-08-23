using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Reconstructs absolute sample timestamps from a device's raw tick counter.
/// </summary>
/// <remarks>
/// The CSV, JSON, and binary/protobuf SD card log parsers each walk their samples in order and
/// accumulate elapsed device-clock time the same way — this now lives here once so the three
/// formats can't drift out of sync with each other, the same reasoning that already unified
/// their per-sample tick delta in <see cref="SdCardTickDelta"/>.
/// </remarks>
/// <param name="tickPeriod">
/// The duration of one device clock tick, in seconds (<c>1 / TimestampFrequency</c>), or
/// <c>0</c> when the frequency is unknown.
/// </param>
internal sealed class SdCardTimestampReconstructor(double tickPeriod)
{
    private uint? _previousTimestamp;
    private double _elapsedSeconds;

    /// <summary>
    /// Whether the device clock's tick period is known. Callers should only call
    /// <see cref="Advance"/> when this is <see langword="true"/> — with an unknown tick period
    /// every sample would report the same elapsed time, which is not a reconstruction so much
    /// as a stall.
    /// </summary>
    public bool HasDeviceClock => tickPeriod > 0;

    /// <summary>
    /// Advances the reconstruction by one sample and returns its absolute timestamp.
    /// </summary>
    /// <param name="rawTimestamp">The sample's raw device tick count.</param>
    /// <param name="baseTime">The absolute time the session anchors its first sample to.</param>
    /// <returns>
    /// <paramref name="baseTime"/> for the first sample seen; for every later sample,
    /// <paramref name="baseTime"/> plus the elapsed device-clock time accumulated since,
    /// computed via <see cref="SdCardTickDelta.Compute"/> so a wraparound of the 32-bit tick
    /// counter is treated as elapsed time rather than time moving backwards.
    /// </returns>
    public DateTime Advance(uint rawTimestamp, DateTime baseTime)
    {
        if (_previousTimestamp == null)
        {
            _previousTimestamp = rawTimestamp;
            return baseTime;
        }

        var delta = SdCardTickDelta.Compute(_previousTimestamp.Value, rawTimestamp);
        _elapsedSeconds += delta * tickPeriod;
        _previousTimestamp = rawTimestamp;

        return baseTime.AddSeconds(_elapsedSeconds);
    }
}
