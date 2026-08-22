namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Computes the elapsed device-clock ticks between two consecutive samples' raw timestamps,
/// accounting for the tick counter wrapping around at <see cref="uint.MaxValue"/>.
/// </summary>
/// <remarks>
/// The CSV, JSON, and binary/protobuf SD card log parsers each read the same device tick
/// counter and previously carried an identical copy of this calculation; it now lives here
/// once so the three formats can't drift out of sync with each other.
/// </remarks>
internal static class SdCardTickDelta
{
    /// <summary>
    /// Returns the number of ticks elapsed from <paramref name="previous"/> to
    /// <paramref name="current"/>, treating a <paramref name="current"/> smaller than
    /// <paramref name="previous"/> as a single wraparound of the 32-bit counter rather than
    /// time moving backwards.
    /// </summary>
    /// <param name="previous">The previous sample's raw tick count.</param>
    /// <param name="current">The current sample's raw tick count.</param>
    /// <returns>The elapsed ticks, always non-negative.</returns>
    public static long Compute(uint previous, uint current)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        // Rollover: ticks remaining to max + current
        return (long)(uint.MaxValue - previous) + current + 1;
    }
}
