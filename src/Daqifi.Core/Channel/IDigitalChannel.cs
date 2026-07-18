namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
public interface IDigitalChannel : IChannel
{
    /// <summary>
    /// Gets or sets the output value for digital output channels (true = high, false = low).
    /// </summary>
    bool OutputValue { get; set; }

    /// <summary>
    /// Gets whether the digital input is currently high (true) or low (false).
    /// </summary>
    bool IsHigh { get; }

    /// <summary>
    /// Gets whether this channel's hardware supports PWM output.
    /// </summary>
    bool IsPwmCapable { get; }

    /// <summary>
    /// Gets or sets whether PWM output is enabled on this channel. This is local bookkeeping
    /// mirroring the last commanded state; use <c>IStreamingDevice.SetPwmEnabled</c> to change
    /// the device.
    /// </summary>
    bool IsPwmEnabled { get; set; }

    /// <summary>
    /// Gets or sets the last commanded PWM duty cycle for this channel, in whole percent.
    /// Local bookkeeping; use <c>IStreamingDevice.SetPwmDutyCycle</c> to change the device.
    /// Defaults to <c>50</c> (a commandable value) until a duty cycle has been set, and clamps
    /// to <c>1-100</c> on assignment so this always holds a commandable value.
    /// </summary>
    int PwmDutyCyclePercent { get; set; }
}
