using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Device;

/// <summary>
/// Represents a device that supports data streaming.
/// </summary>
/// <remarks>
/// <para>
/// The synchronous commands here are fire-and-forget: each sends and returns, and parses no reply.
/// An <c>...Async</c> member of this interface means one thing: its task completes once the device
/// has confirmed the command, and fails with <see cref="DeviceCommandFailedException"/> when the
/// device refuses it or does not confirm it. What counts as confirmation is stated on each member.
/// </para>
/// <para>
/// Extends <see cref="IConfirmingDeviceAdministration"/>, so the calibration commands, which exist
/// only in confirming form, are available directly on this interface with no cast needed.
/// </para>
/// </remarks>
public interface IStreamingDevice : IDevice, IConfirmingDeviceAdministration
{
    /// <summary>
    /// Gets the device metadata containing part number, firmware version, capabilities, etc.
    /// </summary>
    DeviceMetadata Metadata { get; }

    /// <summary>
    /// Gets the collection of channels populated from device status messages. Every
    /// enable/disable/DIO/PWM method below documents that the channel it is given "must belong
    /// to this device's <c>Channels</c> collection" — this member is what makes that
    /// requirement satisfiable from an <see cref="IStreamingDevice"/> reference alone, with no
    /// cast to the concrete device type (#333).
    /// </summary>
    /// <remarks>
    /// This is a live view over the backing collection; callers that fold over channels off the
    /// consumer thread should prefer <see cref="GetChannelsSnapshot"/> instead, for the same
    /// reason documented there.
    /// </remarks>
    IReadOnlyList<IChannel> Channels { get; }

    /// <summary>
    /// Returns a point-in-time snapshot of the channel collection, safe to enumerate even when
    /// a status message repopulates the collection concurrently on the consumer thread.
    /// </summary>
    /// <returns>A lock-protected copy of the current channel collection.</returns>
    IReadOnlyList<IChannel> GetChannelsSnapshot();

    /// <summary>
    /// Occurs when channels have been populated from a device status message.
    /// </summary>
    event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated;

    /// <summary>
    /// Gets or sets the streaming frequency in Hz.
    /// </summary>
    int StreamingFrequency { get; set; }

    /// <summary>
    /// Gets the highest streaming frequency in Hz this device can actually sustain with the
    /// channels it has enabled right now — which is lower, usually far lower, than the absolute
    /// ceiling <see cref="StreamingFrequency"/> validates against.
    /// </summary>
    /// <remarks>
    /// Zero is a real answer: it means no analog input is enabled, so there is no sampling
    /// cadence to set a rate for. A digital-only selection reads zero too — digital pins are
    /// captured on the analog sample tick rather than driving one of their own, and the device
    /// reports zero for that selection itself. The exception is a device that has stated
    /// nothing about how its channel set affects the rate, which reports the board ceiling
    /// whatever is enabled; see <see cref="SampleRateCap"/> for that case, for where the figure
    /// comes from, and for how fresh it is.
    /// </remarks>
    int MaximumStreamingFrequencyHz => SampleRateCap.ComputeForDevice(this);

    /// <summary>
    /// Gets a value indicating whether the device is currently streaming data.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Starts streaming data from the device, at no more than
    /// <see cref="MaximumStreamingFrequencyHz"/>.
    /// </summary>
    /// <remarks>
    /// A rate above what the enabled channels sustain is lowered to the cap rather than sent.
    /// The device would otherwise answer the start with SCPI <c>-222</c> and stream nothing,
    /// which a caller cannot tell apart from a quiet signal (issue #899). Read
    /// <see cref="StreamingFrequency"/> afterwards for the rate the stream is running at.
    /// </remarks>
    void StartStreaming();

    /// <summary>
    /// Starts streaming, and completes once the device has confirmed that it is streaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed means one of two things: a stream frame arrived, or, when none arrived in the
    /// first few sample periods, the device was asked and answered that it is streaming. Use
    /// <see cref="StartStreaming"/> to send the command without waiting for either.
    /// </para>
    /// <para>
    /// When a stream is already running no start is sent; the running stream is confirmed the
    /// same way.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// A cancellation token to observe. Cancelling after the command was sent abandons the
    /// confirmation, not the stream.
    /// </param>
    /// <returns>A task that completes once the device has confirmed the stream.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device refused the start, reports that it is not streaming, or did not answer.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled, or another caller stops the stream before it is confirmed.
    /// </exception>
    Task StartStreamingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops streaming data from the device.
    /// </summary>
    /// <remarks>
    /// Does nothing when <see cref="IsStreaming"/> is <c>false</c>.
    /// </remarks>
    void StopStreaming();

    /// <summary>
    /// Stops streaming, and completes once the device has confirmed that it is not streaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed means the device was asked, after the stop, whether it is streaming and answered
    /// that it is not. The stop is sent whether or not <see cref="IsStreaming"/> is set, so this
    /// also ends a stream this object did not start, and can be called again after a failure. Use
    /// <see cref="StopStreaming"/> to send the command without waiting.
    /// </para>
    /// <para>
    /// The device answers only after it has closed an SD log the stop ended, or after its own
    /// limit for that, so ending a log can take several seconds. A confirmed stop means sampling
    /// has ended; it does not mean the log file reached the card intact.
    /// </para>
    /// <para>
    /// It waits its turn behind another operation that has exclusive use of the device.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// A cancellation token to observe. Cancelling after the command was sent abandons the
    /// confirmation, not the stop.
    /// </param>
    /// <returns>A task that completes once the device has confirmed the stop.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device reports that it is still streaming, or did not answer.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task StopStreamingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables a single channel and reconfigures the device accordingly.
    /// </summary>
    /// <param name="channel">The channel to enable. Must belong to this device's <c>Channels</c> collection.</param>
    /// <remarks>
    /// For analog channels the device-level ADC enable bitmask is recomputed over all enabled
    /// analog channels and sent to the device. For digital channels the global DIO enable state
    /// is updated. The channel's <see cref="IChannel.IsEnabled"/> flag is set to <c>true</c>.
    /// </remarks>
    void EnableChannel(IChannel channel);

    /// <summary>
    /// Enables multiple channels in a single operation, sending at most one command per channel type.
    /// </summary>
    /// <param name="channels">The channels to enable. Each must belong to this device's <c>Channels</c> collection.</param>
    /// <remarks>
    /// Each channel's <see cref="IChannel.IsEnabled"/> flag is set to <c>true</c>. The device then
    /// receives at most one command per affected channel type: the recomputed ADC enable bitmask for
    /// analog channels and the global DIO enable for digital channels. The input is enumerated once.
    /// </remarks>
    void EnableChannels(IEnumerable<IChannel> channels);

    /// <summary>
    /// Disables a single channel and reconfigures the device accordingly.
    /// </summary>
    /// <param name="channel">The channel to disable. Must belong to this device's <c>Channels</c> collection.</param>
    /// <remarks>
    /// Sets the channel's <see cref="IChannel.IsEnabled"/> flag to <c>false</c>. For analog channels the
    /// ADC enable bitmask is recomputed over the remaining enabled analog channels; for digital channels
    /// the global DIO enable state is updated.
    /// </remarks>
    void DisableChannel(IChannel channel);

    /// <summary>
    /// Disables all channels on the device.
    /// </summary>
    void DisableAllChannels();

    /// <summary>
    /// Sets the direction (input or output) of a digital I/O channel.
    /// </summary>
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Rejected if <see cref="Channel.IDigitalChannel.IsPwmEnabled"/> is <c>true</c> on this channel — see
    /// <see cref="SetPwmEnabled"/>.</param>
    /// <param name="direction">The direction to apply. Must be <see cref="ChannelDirection.Input"/> or <see cref="ChannelDirection.Output"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when PWM is enabled on <paramref name="channel"/>; the
    /// firmware ignores direction writes while PWM drives the pin. Call <see cref="SetPwmEnabled"/> with
    /// <c>enabled: false</c> first.</exception>
    void SetDioDirection(IChannel channel, ChannelDirection direction);

    /// <summary>
    /// Sets the output state (high or low) of a digital I/O channel.
    /// </summary>
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Rejected if <see cref="Channel.IDigitalChannel.IsPwmEnabled"/> is <c>true</c> on this channel — see
    /// <see cref="SetPwmEnabled"/>.</param>
    /// <param name="value"><c>true</c> to drive the output high; <c>false</c> to drive it low.</param>
    /// <exception cref="InvalidOperationException">Thrown when PWM is enabled on <paramref name="channel"/>; the
    /// firmware ignores state writes while PWM drives the pin. Call <see cref="SetPwmEnabled"/> with
    /// <c>enabled: false</c> first.</exception>
    void SetDioValue(IChannel channel, bool value);

    /// <summary>
    /// Enables or disables PWM output on a PWM-capable digital channel.
    /// </summary>
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Enabling requires <see cref="Channel.IDigitalChannel.IsPwmCapable"/>; disabling is accepted on any
    /// digital channel.</param>
    /// <param name="enabled"><c>true</c> to start PWM output; <c>false</c> to stop it.</param>
    /// <remarks>
    /// Set the duty cycle (and, once per session, the shared frequency) before enabling — see
    /// <see cref="SetPwmDutyCycle"/> and <see cref="SetPwmFrequency"/>. While PWM is enabled the firmware
    /// ignores digital direction/state writes for the channel at the hardware level. Disabling leaves the
    /// pin transiently high-impedance, but the firmware keeps the channel's stored direction (mirrored by
    /// <see cref="Channel.IChannel.Direction"/>), and any subsequent <see cref="SetDioValue"/> or
    /// <see cref="SetDioDirection"/> write — or the per-tick refresh while streaming — re-applies it and
    /// resumes driving, so no explicit direction resend is required first.
    /// <para>
    /// Core records PWM as off on every channel when the connection ends (#945): the device stops
    /// PWM if it restarts, and keeps it running if only the link was lost, and Core cannot tell
    /// which happened. Call this again after a reconnect — <c>true</c> to carry on, <c>false</c> to stop.
    /// </para>
    /// </remarks>
    void SetPwmEnabled(IChannel channel, bool enabled);

    /// <summary>
    /// Sets the PWM duty cycle of a PWM-capable digital channel.
    /// </summary>
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection
    /// and be <see cref="Channel.IDigitalChannel.IsPwmCapable"/>.</param>
    /// <param name="dutyCyclePercent">The duty cycle in whole percent, 1-100. A duty of 0 is rejected
    /// because the firmware stores but never applies it (the old duty keeps toggling); stop the output
    /// with <see cref="SetPwmEnabled"/> instead.</param>
    void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent);

    /// <summary>
    /// Sets the PWM frequency, in hertz, for the whole device.
    /// </summary>
    /// <param name="frequencyHz">The frequency in hertz, from the device's floor up to 50000. The floor is
    /// 6 Hz (<see cref="DaqifiStreamingDevice.MinPwmFrequencyHz"/>), or on firmware v3.9.0 and newer the
    /// floor the device advertises (<see cref="DaqifiStreamingDevice.PwmFrequencyFloorHz"/>). Values below it
    /// are rejected because the firmware's 16-bit period register silently wraps for them, producing a
    /// kilohertz-range output.</param>
    /// <remarks>
    /// All PWM channels share one hardware timer, so this applies to every PWM channel at once — there is
    /// no per-channel frequency. Changing it while channels are enabled takes effect live and rescales
    /// each enabled channel's duty cycle.
    /// </remarks>
    void SetPwmFrequency(int frequencyHz);

    /// <summary>
    /// Gets the last PWM frequency commanded through <see cref="SetPwmFrequency"/> this
    /// session, in hertz. Local bookkeeping only — a device keeps its PWM state across host
    /// disconnects, so this defaults to a commandable value (see
    /// <see cref="DaqifiStreamingDevice.DefaultPwmFrequencyHz"/>) rather than 0, and does not
    /// prove the device is actually running at that frequency.
    /// </summary>
    int PwmFrequencyHz { get; }

    /// <summary>
    /// Sets the analog output (DAC) voltage of a channel and applies it immediately.
    /// </summary>
    /// <param name="channelNumber">The analog output channel number.</param>
    /// <param name="voltage">
    /// The output voltage, in volts. Must be a finite number, and within the channel's stated
    /// range when the device described one (see
    /// <see cref="Daqifi.Core.Channel.IAnalogOutputChannel"/>).
    /// </param>
    /// <remarks>
    /// Analog output is available on NQ3 hardware only. Each call stages the level and then latches it
    /// immediately, so it is not suitable for synchronized multi-channel updates — use
    /// <see cref="DaqifiStreamingDevice.StageAnalogOutput"/> and
    /// <see cref="DaqifiStreamingDevice.LatchAnalogOutputs"/> for that.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The channel number is negative, or the voltage falls outside the range the device stated
    /// for a channel it described.
    /// </exception>
    /// <exception cref="FeatureNotSupportedException">The known board does not support analog output.</exception>
    void SetAnalogOutput(int channelNumber, double voltage);

    /// <summary>
    /// Reboots the device and disconnects from it.
    /// </summary>
    /// <remarks>
    /// Sends the reboot command and then tears down the local connection, since the device
    /// drops its link while restarting. Reconnect once the device is back online.
    /// </remarks>
    void Reboot();

    /// <summary>
    /// Reboots the device, and completes once the device has confirmed that it is restarting and
    /// this side has disconnected from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed means the device acknowledged the command, which it does just before it resets.
    /// It does not mean the device is back: reconnect once it is. Use <see cref="Reboot"/> to send
    /// the command and disconnect without waiting.
    /// </para>
    /// <para>
    /// A running stream is stopped first, because the acknowledgement cannot be read out of one.
    /// </para>
    /// <para>
    /// Once the command has been sent the connection is closed, whether or not this throws, with
    /// one exception: a device that answers with a refusal is still there, so it stays connected.
    /// Without an acknowledgement the outcome is unknown and this throws. Over WiFi the device
    /// shuts its network down immediately after acknowledging, so a lost acknowledgement does not
    /// mean the reboot failed.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// A cancellation token to observe. Cancelling after the command was sent abandons the
    /// confirmation, not the reboot.
    /// </param>
    /// <returns>A task that completes once the reboot is confirmed and the connection is closed.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device refused the reboot, or did not acknowledge it.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task RebootAsync(CancellationToken cancellationToken = default);
}
