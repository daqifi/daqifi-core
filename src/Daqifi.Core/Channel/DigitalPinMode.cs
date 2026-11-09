namespace Daqifi.Core.Channel;

/// <summary>
/// The one mode a digital pin is in at a time, as last commanded through Core or reported by
/// the device.
/// </summary>
public enum DigitalPinMode
{
    /// <summary>
    /// The pin is a digital input.
    /// </summary>
    Input,

    /// <summary>
    /// The pin is a digital output, driven high or low.
    /// </summary>
    Output,

    /// <summary>
    /// The pin is driven by the PWM timer. The firmware ignores digital direction and level
    /// commands for the pin until PWM is disabled.
    /// </summary>
    Pwm
}
