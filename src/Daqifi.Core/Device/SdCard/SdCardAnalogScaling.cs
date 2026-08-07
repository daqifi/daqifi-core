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
        for (var ch = 0; ch < rawValues.Count; ch++)
        {
            result[ch] = ScaleChannel(rawValues[ch], ch, config);
        }

        return result;
    }

    /// <summary>
    /// Scales raw ADC integer counts (the protobuf log format) to real voltage. Behaves
    /// identically to the <see cref="IReadOnlyList{Double}"/> overload, but reads each raw
    /// count directly instead of requiring the caller to pre-convert to <c>double</c>,
    /// avoiding an intermediate array when scaling is applied.
    /// </summary>
    /// <param name="rawValues">The per-channel raw ADC counts for one sample.</param>
    /// <param name="config">Same as the <see cref="IReadOnlyList{Double}"/> overload.</param>
    /// <returns>The scaled values, in volts, or <paramref name="rawValues"/> converted to <c>double</c> unscaled if no config is available.</returns>
    internal static IReadOnlyList<double> ScaleRawAnalogValues(
        IReadOnlyList<int> rawValues,
        SdCardDeviceConfiguration? config)
    {
        var result = new double[rawValues.Count];

        if (config == null || config.Resolution == 0)
        {
            // No config or resolution available — copy the raw int counts through unscaled
            for (var ch = 0; ch < rawValues.Count; ch++)
            {
                result[ch] = rawValues[ch];
            }

            return result;
        }

        for (var ch = 0; ch < rawValues.Count; ch++)
        {
            result[ch] = ScaleChannel(rawValues[ch], ch, config);
        }

        return result;
    }

    private static double ScaleChannel(double rawValue, int channel, SdCardDeviceConfiguration config)
    {
        var cal = config.CalibrationValues; // May be null — defaults applied per-channel below
        var portRange = config.PortRange;
        var intScale = config.InternalScaleM;

        var calM = cal != null && channel < cal.Count ? cal[channel].Slope : 1.0;
        var calB = cal != null && channel < cal.Count ? cal[channel].Intercept : 0.0;
        var range = portRange != null && channel < portRange.Count ? portRange[channel] : 1.0;
        var scaleM = intScale != null && channel < intScale.Count ? intScale[channel] : 1.0;

        return AnalogScaling.Scale(rawValue, config.Resolution, range, calM, scaleM, calB);
    }
}
