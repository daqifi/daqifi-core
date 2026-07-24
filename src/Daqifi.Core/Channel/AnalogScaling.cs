namespace Daqifi.Core.Channel;

/// <summary>
/// The single implementation of the raw-ADC-count to volts conversion, shared by live channel
/// scaling (<see cref="AnalogChannel.GetScaledValue"/>) and the SD-card log parsers so the two
/// paths can never disagree about the same sample.
/// </summary>
internal static class AnalogScaling
{
    /// <summary>
    /// Converts a raw ADC count to volts using the device's calibration parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The formula mirrors the firmware's own on-device conversion (<c>MC12bADC.c</c>, the path
    /// behind <c>MEASure:VOLTage:DC?</c>):
    /// </para>
    /// <code>
    /// Volts = (raw / Resolution) * PortRange * CalibrationM * InternalScaleM + CalibrationB
    /// </code>
    /// <para>
    /// <paramref name="calibrationB"/> is an offset in volts and is added <em>after</em>
    /// <paramref name="internalScaleM"/> is applied — it is deliberately not scaled by it. Scaling
    /// the offset makes host-side values diverge from the device's own <c>MEAS:VOLT:DC?</c> reading
    /// whenever the internal scale factor is non-unity and the offset is non-zero.
    /// </para>
    /// </remarks>
    /// <param name="rawValue">The raw ADC count.</param>
    /// <param name="resolution">The ADC's maximum raw count (2^bits - 1). Must be non-zero.</param>
    /// <param name="portRange">The channel's full-scale range, in volts.</param>
    /// <param name="calibrationM">The calibration slope (gain).</param>
    /// <param name="internalScaleM">The board's internal (front-end) scale factor.</param>
    /// <param name="calibrationB">The calibration offset, in volts.</param>
    /// <returns>The scaled value, in volts.</returns>
    /// <remarks>
    /// The parameters are ordered to match the formula left-to-right — the multiplicative terms in
    /// order, then the additive offset last — so a call site reads as the formula does and the two
    /// easily-transposed factors (<paramref name="internalScaleM"/> and
    /// <paramref name="calibrationB"/>) are not interchangeable by eye.
    /// </remarks>
    internal static double Scale(
        double rawValue,
        double resolution,
        double portRange,
        double calibrationM,
        double internalScaleM,
        double calibrationB)
    {
        var normalized = rawValue / resolution;
        return normalized * portRange * calibrationM * internalScaleM + calibrationB;
    }
}
