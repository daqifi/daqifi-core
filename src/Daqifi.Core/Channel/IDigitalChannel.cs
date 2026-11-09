namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
public interface IDigitalChannel : IChannel
{
    /// <summary>
    /// Gets or sets the output value for digital output channels (true = high, false = low).
    /// This is local bookkeeping mirroring the last commanded state; use
    /// <c>IStreamingDevice.SetDioValue</c> to change the device.
    /// </summary>
    [Obsolete(
        "Read IsHigh instead: it now reports the pin's streamed state for an output as well as an input. "
        + "To change the pin, call IStreamingDevice.SetDioValue. This property is a local note of the last "
        + "commanded value and is never confirmed by the device. It will be removed in a future major version.")]
    bool OutputValue { get; set; }

    /// <summary>
    /// Gets whether the pin is currently high (true) or low (false), as reported by the most
    /// recent streamed sample. Valid for an input pin and for a driven output pin alike: the
    /// firmware reads the pin back, not the direction register (#901). Returns
    /// <see langword="false"/> until the first sample arrives.
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
    bool IsPwmEnabled
    {
        get;

        [Obsolete(
            "Call IStreamingDevice.SetPwmEnabled to start or stop PWM, and read Mode or IsPwmEnabled to see it. "
            + "Assigning here sends nothing to the device, and Core then refuses digital commands for a pin "
            + "that is not in PWM mode. This setter will be removed in a future major version.")]
        set;
    }

    /// <summary>
    /// Gets the one mode the pin is in: input, output or PWM.
    /// </summary>
    /// <remarks>
    /// Core records PWM as off when a connection ends, because it can no longer vouch for the
    /// device; enable PWM again after a reconnect (#945). The default implementation derives the
    /// mode from <see cref="IsPwmEnabled"/> and <see cref="IChannel.Direction"/>, so an existing
    /// implementation of this interface keeps compiling.
    /// </remarks>
    DigitalPinMode Mode =>
        IsPwmEnabled ? DigitalPinMode.Pwm
        : Direction == ChannelDirection.Output ? DigitalPinMode.Output
        : DigitalPinMode.Input;

    /// <summary>
    /// Gets or sets the last commanded PWM duty cycle for this channel, in whole percent.
    /// Local bookkeeping; use <c>IStreamingDevice.SetPwmDutyCycle</c> to change the device.
    /// Defaults to <c>50</c> (a commandable value) until a duty cycle has been set, and clamps
    /// to <c>1-100</c> on assignment so this always holds a commandable value.
    /// </summary>
    int PwmDutyCyclePercent { get; set; }
}
