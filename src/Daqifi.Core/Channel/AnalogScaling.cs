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
    /// Volts = (raw / FullScaleCount) * PortRange * CalibrationSlope * InternalScaleM + CalibrationOffset
    /// </code>
    /// <para>
    /// <paramref name="calibrationOffset"/> is an offset in volts and is added <em>after</em>
    /// <paramref name="internalScaleM"/> is applied — it is deliberately not scaled by it. Scaling
    /// the offset makes host-side values diverge from the device's own <c>MEAS:VOLT:DC?</c> reading
    /// whenever the internal scale factor is non-unity and the offset is non-zero.
    /// </para>
    /// <para>
    /// Parameters follow the formula left to right (offset last), so the easily transposed
    /// <paramref name="internalScaleM"/> and <paramref name="calibrationOffset"/> read in formula order
    /// at every call site.
    /// </para>
    /// </remarks>
    /// <param name="rawValue">The raw ADC count.</param>
    /// <param name="fullScaleCount">The ADC's maximum raw count (2^bits - 1). Must be non-zero.</param>
    /// <param name="portRange">The channel's full-scale range, in volts.</param>
    /// <param name="calibrationSlope">The calibration slope (gain).</param>
    /// <param name="internalScaleM">The board's internal (front-end) scale factor.</param>
    /// <param name="calibrationOffset">The calibration offset, in volts.</param>
    /// <returns>The scaled value, in volts.</returns>
    internal static double Scale(
        double rawValue,
        double fullScaleCount,
        double portRange,
        double calibrationSlope,
        double internalScaleM,
        double calibrationOffset)
    {
        var normalized = rawValue / fullScaleCount;
        return normalized * portRange * calibrationSlope * internalScaleM + calibrationOffset;
    }
}
