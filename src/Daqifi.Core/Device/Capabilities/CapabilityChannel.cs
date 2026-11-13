namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// One entry of the capability document's flat <c>channels[]</c> array.
/// </summary>
/// <remarks>
/// Every property except <see cref="Id"/> and <see cref="Kind"/> is nullable or defaulted, because
/// the schema emits a different property set per <see cref="Kind"/> (an analog input carries
/// ranges and calibration; a digital pin carries a <c>features</c> object) and because a field the
/// document omits must read as "not stated", never as a value.
/// </remarks>
public sealed class CapabilityChannel
{
    /// <summary>
    /// Gets the channel's device-facing index. Unique only within a <see cref="Kind"/> — the
    /// document numbers analog inputs and digital pins from 0 independently.
    /// </summary>
    public int Id { get; init; }

    /// <summary>Gets the channel kind.</summary>
    public CapabilityChannelKind Kind { get; init; }

    /// <summary>
    /// Gets the raw <c>kind</c> string as emitted by the device, retained so an unrecognized kind
    /// (<see cref="CapabilityChannelKind.Unknown"/>) is still diagnosable.
    /// </summary>
    public string? RawKind { get; init; }

    /// <summary>Gets the measured quantity, e.g. <c>"voltage"</c> or <c>"temperature"</c>.</summary>
    public string? SignalType { get; init; }

    /// <summary>Gets the unit for <see cref="SignalType"/>, e.g. <c>"V"</c> or <c>"Cel"</c>.</summary>
    public string? Unit { get; init; }

    /// <summary>Gets the converter resolution in bits, or <c>null</c> when not stated.</summary>
    public int? ResolutionBits { get; init; }

    /// <summary>
    /// Gets a value indicating whether the channel is sampled by a dedicated converter (zero
    /// inter-channel skew) rather than through a shared multiplexer. This is the "Type-1" count
    /// that feeds <see cref="CapabilityRateModel.TryComputeMaxRateHz"/>.
    /// </summary>
    public bool IsSimultaneous { get; init; }

    /// <summary>
    /// Gets how long after a sample's timestamp this analog input actually converts, in ticks of
    /// <see cref="CapabilityTiming.TimestampHz"/>; or <c>null</c> when not stated (firmware before
    /// v3.9.0).
    /// </summary>
    /// <remarks>
    /// Every channel in a sample shares one timestamp, but multiplexed channels convert one after
    /// another, so each is offset from that stamp by its position in the scan. Divide by
    /// <see cref="CapabilityTiming.TimestampHz"/> for seconds. <c>0</c> means no deterministic
    /// offset applies: the channel is <see cref="IsSimultaneous"/>, or it is not in the scan. The
    /// values describe the channels enabled when the document was read, so re-read it after
    /// changing the enabled set.
    /// </remarks>
    public int? ScanOffsetTicks { get; init; }

    /// <summary>Gets a value indicating whether the channel can be configured as differential.</summary>
    public bool IsDifferential { get; init; }

    /// <summary>Gets the low end of the channel's terminal range, or <c>null</c> when not stated.</summary>
    public double? RangeMinimum { get; init; }

    /// <summary>Gets the high end of the channel's terminal range, or <c>null</c> when not stated.</summary>
    public double? RangeMaximum { get; init; }

    /// <summary>
    /// Gets a value indicating whether a <see cref="CapabilityChannelKind.DigitalIo"/> pin
    /// advertises PWM output. The schema uses key presence for this: a pin without PWM simply
    /// omits the <c>pwm</c> object from its <c>features</c>.
    /// </summary>
    public bool SupportsPwm { get; init; }

    /// <summary>
    /// Gets the lowest PWM frequency the pin advertises, or <c>null</c> when not stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trustworthy from firmware v3.9.0 (<see cref="DeviceFeature.PwmFrequencyFloor"/>). There the
    /// firmware computes the floor from the peripheral clock the unit actually runs at
    /// (ceil(PBCLK3 / 256 / 65535), the lowest frequency whose period fits the 16-bit timer at its
    /// largest prescaler) and refuses <c>DIO:PWM:FREQ</c> below it, so the value can differ from
    /// unit to unit.
    /// </para>
    /// <para>
    /// Earlier firmware advertises a fixed 1 Hz here that the hardware cannot produce: below about
    /// 6 Hz the 16-bit period register silently wraps and the output runs in the kilohertz range.
    /// For those devices Core uses <see cref="DaqifiStreamingDevice.MinPwmFrequencyHz"/> instead.
    /// <see cref="DaqifiStreamingDevice.PwmFrequencyFloorHz"/> picks the right one for the
    /// connected device and is the floor <see cref="DaqifiStreamingDevice.SetPwmFrequency"/>
    /// enforces.
    /// </para>
    /// </remarks>
    public int? PwmMinimumFrequencyHz { get; init; }

    /// <summary>Gets the highest PWM frequency the pin accepts, or <c>null</c> when not stated.</summary>
    public int? PwmMaximumFrequencyHz { get; init; }

    /// <summary>
    /// Gets the device-reported linear calibration slope, or <c>null</c> when not stated. Together
    /// with <see cref="CalibrationIntercept"/> this is the device's own raw-to-engineering-units
    /// conversion.
    /// </summary>
    public double? CalibrationSlope { get; init; }

    /// <summary>Gets the device-reported linear calibration intercept, or <c>null</c> when not stated.</summary>
    public double? CalibrationIntercept { get; init; }
}
