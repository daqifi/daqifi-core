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
    /// Gets the resolution of the ADC (e.g., 65535 for 16-bit).
    /// </summary>
    uint Resolution { get; }

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
