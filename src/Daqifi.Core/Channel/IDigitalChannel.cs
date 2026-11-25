namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
public interface IDigitalChannel : IChannel
{
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
    /// Gets whether PWM output is enabled on this channel. This is local bookkeeping mirroring
    /// the last commanded state; use <c>IStreamingDevice.SetPwmEnabled</c> to change the device.
    /// </summary>
    bool IsPwmEnabled { get; }

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
    /// Gets the last commanded PWM duty cycle for this channel, in whole percent. Use
    /// <c>IStreamingDevice.SetPwmDutyCycle</c> to change it. <c>50</c> (a commandable value) until
    /// a duty cycle has been set.
    /// </summary>
    int PwmDutyCyclePercent { get; }
}
