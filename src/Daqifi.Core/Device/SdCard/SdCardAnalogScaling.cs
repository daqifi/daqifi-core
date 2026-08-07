using System.Collections.Generic;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Shared raw-value scaling for the SD-card log parsers, so the protobuf, CSV, and JSON
/// paths can never disagree about the same sample.
/// </summary>
internal static class SdCardAnalogScaling
{
    /// <summary>
    /// Scales raw ADC values to real voltage using device calibration data.
    /// Formula: <c>raw / resolution * portRange * calM * internalScaleM + calB</c>
    /// (see <see cref="AnalogScaling"/> — <c>calB</c> is an offset in volts and is
    /// deliberately not scaled by <c>internalScaleM</c>).
    /// </summary>
    /// <param name="rawValues">The per-channel raw ADC counts for one sample.</param>
    /// <param name="config">
    /// The device configuration supplying resolution, port range, calibration, and internal
    /// scale. When <c>null</c> or <see cref="SdCardDeviceConfiguration.Resolution"/> is zero,
    /// <paramref name="rawValues"/> is returned unchanged.
    /// </param>
    /// <returns>The scaled values, in volts, or <paramref name="rawValues"/> as-is if no config is available.</returns>
    internal static IReadOnlyList<double> ScaleRawAnalogValues(
        IReadOnlyList<double> rawValues,
        SdCardDeviceConfiguration? config)
    {
        if (config == null || config.Resolution == 0)
        {
            // No config or resolution available — return raw values as-is
            return rawValues;
        }

        var result = new double[rawValues.Count];
        var resolution = (double)config.Resolution;
        var cal = config.CalibrationValues; // May be null — defaults applied per-channel below
        var portRange = config.PortRange;
        var intScale = config.InternalScaleM;

        for (var ch = 0; ch < rawValues.Count; ch++)
        {
            var calM = cal != null && ch < cal.Count ? cal[ch].Slope : 1.0;
            var calB = cal != null && ch < cal.Count ? cal[ch].Intercept : 0.0;
            var range = portRange != null && ch < portRange.Count ? portRange[ch] : 1.0;
            var scaleM = intScale != null && ch < intScale.Count ? intScale[ch] : 1.0;

            result[ch] = AnalogScaling.Scale(rawValues[ch], resolution, range, calM, scaleM, calB);
        }

        return result;
    }
}
