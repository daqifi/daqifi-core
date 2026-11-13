namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>streaming.timing</c> block of the capability document: the device's streaming timebase.
/// Published by firmware v3.8.0 and newer.
/// </summary>
/// <remarks>
/// Every value is <c>null</c> when the document did not state it.
/// </remarks>
public sealed class CapabilityTiming
{
    /// <summary>
    /// Gets the rate in Hz of the timer that stamps each streamed sample, or <c>null</c> when not
    /// stated. Also the unit of <see cref="CapabilityChannel.ScanOffsetTicks"/>: divide a tick count
    /// by this to get seconds.
    /// </summary>
    public int? TimestampHz { get; init; }

    /// <summary>Gets the rate in Hz of the timer that paces sampling, or <c>null</c> when not stated.</summary>
    public int? StreamTimerHz { get; init; }

    /// <summary>
    /// Gets the exact <see cref="TimestampHz"/> tick interval between consecutive samples at the
    /// configured rate, or <c>null</c> when not stated. <c>0</c> until a sample rate is configured.
    /// </summary>
    public int? TimestampTicksPerSample { get; init; }

    /// <summary>
    /// Gets the sample rate the hardware actually runs, in millihertz, or <c>null</c> when not
    /// stated. It can differ slightly from the requested rate, because the timer period is an
    /// integer. <c>0</c> until a sample rate is configured.
    /// </summary>
    public int? ActualRateMillihertz { get; init; }

    /// <summary>Gets the peripheral clock the device is running, in Hz, or <c>null</c> when not stated.</summary>
    public int? PeripheralClockHz { get; init; }

    /// <summary>
    /// Gets the peripheral clock the firmware was built for, in Hz, or <c>null</c> when not stated.
    /// </summary>
    public int? PeripheralClockBuiltHz { get; init; }

    /// <summary>
    /// Gets whether the running clock matches the one the firmware was built for, or <c>null</c>
    /// when not stated. <c>false</c> means every rate the device produces is scaled by
    /// <see cref="PeripheralClockHz"/> / <see cref="PeripheralClockBuiltHz"/> (for example, about
    /// 79% on a unit whose clock configuration a firmware update cannot change), and the unit
    /// needs a physical reprogram to reach its intended clock.
    /// </summary>
    public bool? ClockMatchesBuild { get; init; }
}
