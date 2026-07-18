namespace Daqifi.Core.Channel;

/// <summary>
/// Represents an analog input/output channel with scaling and calibration capabilities.
/// </summary>
public interface IAnalogChannel : IChannel
{
    /// <summary>
    /// Gets the minimum value for the channel range.
    /// </summary>
    double MinValue { get; set; }

    /// <summary>
    /// Gets the maximum value for the channel range.
    /// </summary>
    double MaxValue { get; set; }

    /// <summary>
    /// Gets whether the configured display range (<see cref="MinValue"/>..<see cref="MaxValue"/>) is
    /// bipolar (spans negative voltages, as ±1V/±5V/±10V differential ranges do) rather than unipolar
    /// (0V-and-up). Lets range-selection UI branch on polarity without hardcoding per-device assumptions.
    /// </summary>
    bool IsBipolar { get; }

    /// <summary>
    /// Gets the resolution of the ADC, expressed as the maximum raw count (e.g., 65535 for 16-bit,
    /// 262143 for 18-bit) — i.e. 2^bits - 1, not 2^bits. This value is a direct divisor in
    /// <see cref="GetScaledValue"/>.
    /// </summary>
    uint Resolution { get; }

    /// <summary>
    /// Gets whether <see cref="Resolution"/> is a fallback guess rather than a value the device
    /// actually reported. When <c>true</c>, samples scaled with this channel may be systematically
    /// wrong (e.g. a device that omits its ADC resolution entirely).
    /// </summary>
    bool ResolutionIsAssumed { get; }

    /// <summary>
    /// Gets or sets the calibration slope (M in the scaling formula).
    /// </summary>
    double CalibrationM { get; set; }

    /// <summary>
    /// Gets or sets the calibration offset (B in the scaling formula).
    /// </summary>
    double CalibrationB { get; set; }

    /// <summary>
    /// Gets or sets the internal scale factor.
    /// </summary>
    double InternalScaleM { get; set; }

    /// <summary>
    /// Gets or sets the port range (voltage range).
    /// </summary>
    double PortRange { get; set; }

    /// <summary>
    /// Converts a raw ADC value to a scaled value using the calibration parameters.
    /// Formula: ScaledValue = (RawValue / Resolution * PortRange * CalibrationM + CalibrationB) * InternalScaleM
    /// </summary>
    /// <param name="rawValue">The raw ADC value from the device.</param>
    /// <returns>The scaled value.</returns>
    double GetScaledValue(int rawValue);
}
