using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.Capabilities;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Represents a device that supports data streaming.
/// </summary>
/// <remarks>
/// Extends <see cref="IConfirmingDeviceAdministration"/> so the calibration operations are
/// available with a <see cref="CancellationToken"/> directly on this interface, with no cast
/// needed — see that interface for why the confirming calibration calls exist alongside the
/// <c>void</c> ones declared here. The streaming/channel/DIO/PWM/output/reboot operations below
/// each carry an <c>...Async</c> twin for the same reason. Most of them have no genuine async
/// machinery underneath today, so the default implementation is a thin, cancellable wrapper over
/// the synchronous call — see the remarks on <see cref="StartStreamingAsync"/>. Reboot is the
/// exception: its teardown is a real, potentially-blocking wait, so
/// <see cref="DaqifiStreamingDevice.RebootAsync"/> overrides the default to await
/// <see cref="IDevice.DisconnectAsync"/> instead of calling <see cref="Reboot"/>.
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
    /// Lowers <see cref="StreamingFrequency"/> to <see cref="MaximumStreamingFrequencyHz"/> when
    /// the live rate no longer fits under it — as it will not, once the channel set grows after
    /// the rate was chosen.
    /// </summary>
    /// <returns>The rate that was live before the adjustment, or <c>null</c> when none was needed.</returns>
    int? EnforceStreamingFrequencyCap() => SampleRateCap.EnforceOn(this);

    /// <summary>
    /// Gets a value indicating whether the device is currently streaming data.
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Starts streaming data from the device.
    /// </summary>
    void StartStreaming();

    /// <summary>
    /// Starts streaming data from the device without blocking the calling thread.
    /// </summary>
    /// <remarks>
    /// The default implementation is a thin, cancellable wrapper over <see cref="StartStreaming"/>:
    /// it checks <paramref name="cancellationToken"/> once and then performs the same single,
    /// fire-and-forget write — there is no underlying text exchange to await. Implementers whose
    /// transport genuinely awaits (e.g. a confirming variant) should override it.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe before starting.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartStreaming();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops streaming data from the device.
    /// </summary>
    void StopStreaming();

    /// <summary>
    /// Stops streaming data from the device without blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="cancellationToken">A cancellation token to observe before stopping.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopStreaming();
        return Task.CompletedTask;
    }

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
    /// Enables a single channel and reconfigures the device accordingly, without blocking the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The channel to enable. Must belong to this device's <c>Channels</c> collection.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task EnableChannelAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnableChannel(channel);
        return Task.CompletedTask;
    }

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
    /// Enables multiple channels in a single operation, without blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channels">The channels to enable. Each must belong to this device's <c>Channels</c> collection.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task EnableChannelsAsync(IEnumerable<IChannel> channels, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnableChannels(channels);
        return Task.CompletedTask;
    }

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
    /// Disables a single channel and reconfigures the device accordingly, without blocking the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The channel to disable. Must belong to this device's <c>Channels</c> collection.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task DisableChannelAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisableChannel(channel);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disables all channels on the device.
    /// </summary>
    void DisableAllChannels();

    /// <summary>
    /// Disables all channels on the device, without blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task DisableAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisableAllChannels();
        return Task.CompletedTask;
    }

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
    /// Sets the direction (input or output) of a digital I/O channel, without blocking the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Rejected if <see cref="Channel.IDigitalChannel.IsPwmEnabled"/> is <c>true</c> on this channel — see
    /// <see cref="SetPwmEnabled"/>.</param>
    /// <param name="direction">The direction to apply. Must be <see cref="ChannelDirection.Input"/> or <see cref="ChannelDirection.Output"/>.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    /// <exception cref="InvalidOperationException">Thrown when PWM is enabled on <paramref name="channel"/>; the
    /// firmware ignores direction writes while PWM drives the pin. Call <see cref="SetPwmEnabled"/> with
    /// <c>enabled: false</c> first.</exception>
    Task SetDioDirectionAsync(IChannel channel, ChannelDirection direction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetDioDirection(channel, direction);
        return Task.CompletedTask;
    }

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
    /// Sets the output state (high or low) of a digital I/O channel, without blocking the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Rejected if <see cref="Channel.IDigitalChannel.IsPwmEnabled"/> is <c>true</c> on this channel — see
    /// <see cref="SetPwmEnabled"/>.</param>
    /// <param name="value"><c>true</c> to drive the output high; <c>false</c> to drive it low.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    /// <exception cref="InvalidOperationException">Thrown when PWM is enabled on <paramref name="channel"/>; the
    /// firmware ignores state writes while PWM drives the pin. Call <see cref="SetPwmEnabled"/> with
    /// <c>enabled: false</c> first.</exception>
    Task SetDioValueAsync(IChannel channel, bool value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetDioValue(channel, value);
        return Task.CompletedTask;
    }

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
    /// </remarks>
    void SetPwmEnabled(IChannel channel, bool enabled);

    /// <summary>
    /// Enables or disables PWM output on a PWM-capable digital channel, without blocking the
    /// calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.
    /// Enabling requires <see cref="Channel.IDigitalChannel.IsPwmCapable"/>; disabling is accepted on any
    /// digital channel.</param>
    /// <param name="enabled"><c>true</c> to start PWM output; <c>false</c> to stop it.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetPwmEnabledAsync(IChannel channel, bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetPwmEnabled(channel, enabled);
        return Task.CompletedTask;
    }

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
    /// Sets the PWM duty cycle of a PWM-capable digital channel, without blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection
    /// and be <see cref="Channel.IDigitalChannel.IsPwmCapable"/>.</param>
    /// <param name="dutyCyclePercent">The duty cycle in whole percent, 1-100. A duty of 0 is rejected
    /// because the firmware stores but never applies it (the old duty keeps toggling); stop the output
    /// with <see cref="SetPwmEnabled"/> instead.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetPwmDutyCycleAsync(IChannel channel, int dutyCyclePercent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetPwmDutyCycle(channel, dutyCyclePercent);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the PWM frequency, in hertz, for the whole device.
    /// </summary>
    /// <param name="frequencyHz">The frequency in hertz, 6-50000. Values below 6 Hz are rejected because
    /// the firmware's 16-bit period register silently wraps for them, producing a kilohertz-range output.</param>
    /// <remarks>
    /// All PWM channels share one hardware timer, so this applies to every PWM channel at once — there is
    /// no per-channel frequency. Changing it while channels are enabled takes effect live and rescales
    /// each enabled channel's duty cycle.
    /// </remarks>
    void SetPwmFrequency(int frequencyHz);

    /// <summary>
    /// Sets the PWM frequency, in hertz, for the whole device, without blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="frequencyHz">The frequency in hertz, 6-50000. Values below 6 Hz are rejected because
    /// the firmware's 16-bit period register silently wraps for them, producing a kilohertz-range output.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetPwmFrequencyAsync(int frequencyHz, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetPwmFrequency(frequencyHz);
        return Task.CompletedTask;
    }

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
    void SetAnalogOutput(int channelNumber, double voltage);

    /// <summary>
    /// Sets the analog output (DAC) voltage of a channel and applies it immediately, without
    /// blocking the calling thread.
    /// </summary>
    /// <inheritdoc cref="StartStreamingAsync" path="/remarks" />
    /// <param name="channelNumber">The analog output channel number. DAC channels are addressed by
    /// number; they are not part of the <c>Channels</c> collection (which holds analog inputs).</param>
    /// <param name="voltage">The output voltage, in volts. Must be a finite number.</param>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task SetAnalogOutputAsync(int channelNumber, double voltage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetAnalogOutput(channelNumber, voltage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reboots the device and disconnects from it.
    /// </summary>
    /// <remarks>
    /// Sends the reboot command and then tears down the local connection, since the device
    /// drops its link while restarting. Reconnect once the device is back online.
    /// </remarks>
    void Reboot();

    /// <summary>
    /// Reboots the device and disconnects from it, without blocking the calling thread.
    /// </summary>
    /// <remarks>
    /// The default implementation is a thin, cancellable wrapper over <see cref="Reboot"/>: it
    /// checks <paramref name="cancellationToken"/> once and then performs the same fire-and-forget
    /// reboot command followed by a synchronous local teardown. Implementers whose disconnect is
    /// itself genuinely asynchronous (see <see cref="IDevice.DisconnectAsync"/>) should override
    /// this to await it instead.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe before sending.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task RebootAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reboot();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // ADC calibration and voltage precision.
    //
    // Every command in this block exists in two shapes. The void one is
    // fire-and-forget: it puts the SCPI primitive on the wire and parses no
    // reply, so a device that refuses the command is indistinguishable from
    // one that carried it out. That is not hypothetical — a Nyquist running
    // firmware 3.7.2 answers CONFigure:ADC:LOADcal with -200,"Execution
    // error" when its user bank was never written, and LoadAdcCalibration()
    // returns as though the bank had loaded.
    //
    // The confirming twin, on IConfirmingDeviceAdministration, sends the same
    // primitive and then reads the device's SCPI error queue, throwing
    // DeviceCommandFailedException unless the device confirms it accepted
    // the command. It costs text exchanges and the device's full attention;
    // the void one costs a single write and stays usable mid-stream. Pick by
    // whether a silent no-op is acceptable.
    // -----------------------------------------------------------------

    /// <summary>
    /// Persists the device's current ADC calibration coefficients to the <b>user</b> NVM bank so they survive a reboot.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over the firmware NVM primitive (<c>CONFigure:ADC:SAVEcal</c>). Pair with
    /// <see cref="LoadAdcCalibration"/> to restore them. Use <see cref="SaveFactoryAdcCalibration"/> to write the
    /// <b>factory</b> bank instead, and <see cref="UseAdcCalibration"/> to choose which bank the device applies.
    /// Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.SaveAdcCalibrationAsync"/> when that matters.
    /// </remarks>
    void SaveAdcCalibration();

    /// <summary>
    /// Restores the device's ADC calibration coefficients from the <b>user</b> NVM bank into its runtime.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="SaveAdcCalibration"/> (firmware primitive <c>CONFigure:ADC:LOADcal</c>).
    /// Fire-and-forget: on a unit whose user bank was never written the device answers
    /// <c>-200,"Execution error"</c> and this method still returns normally — use
    /// <see cref="IConfirmingDeviceAdministration.LoadAdcCalibrationAsync"/> when that matters.
    /// </remarks>
    void LoadAdcCalibration();

    /// <summary>
    /// Sets a single channel's ADC calibration slope (CalM) in device RAM.
    /// </summary>
    /// <param name="channelNumber">The analog input channel number.</param>
    /// <param name="calM">The calibration slope (gain) coefficient.</param>
    /// <remarks>
    /// <b>RAM only</b> (firmware primitive <c>CONFigure:ADC:chanCALM</c>). The value is lost on reboot unless
    /// persisted with <see cref="SaveAdcCalibration"/> (user bank) or <see cref="SaveFactoryAdcCalibration"/>
    /// (factory bank). Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.SetAdcCalibrationSlopeAsync"/> when that matters.
    /// </remarks>
    void SetAdcCalibrationSlope(int channelNumber, double calM);

    /// <summary>
    /// Sets a single channel's ADC calibration offset (CalB) in device RAM.
    /// </summary>
    /// <param name="channelNumber">The analog input channel number.</param>
    /// <param name="calB">The calibration offset coefficient.</param>
    /// <remarks>
    /// <b>RAM only</b> (firmware primitive <c>CONFigure:ADC:chanCALB</c>). The value is lost on reboot unless
    /// persisted with <see cref="SaveAdcCalibration"/> (user bank) or <see cref="SaveFactoryAdcCalibration"/>
    /// (factory bank). Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.SetAdcCalibrationOffsetAsync"/> when that matters.
    /// </remarks>
    void SetAdcCalibrationOffset(int channelNumber, double calB);

    /// <summary>
    /// Persists the device's current ADC calibration coefficients to the <b>factory</b> NVM bank.
    /// </summary>
    /// <remarks>
    /// Firmware primitive <c>CONFigure:ADC:SAVEFcal</c>. Contrast with <see cref="SaveAdcCalibration"/>, which
    /// writes the user bank. Which bank the device applies is chosen with <see cref="UseAdcCalibration"/>.
    /// Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.SaveFactoryAdcCalibrationAsync"/> when that matters.
    /// </remarks>
    void SaveFactoryAdcCalibration();

    /// <summary>
    /// Restores the device's ADC calibration coefficients from the <b>factory</b> NVM bank into its runtime.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="SaveFactoryAdcCalibration"/> (firmware primitive <c>CONFigure:ADC:LOADFcal</c>).
    /// Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.LoadFactoryAdcCalibrationAsync"/> when that matters.
    /// </remarks>
    void LoadFactoryAdcCalibration();

    /// <summary>
    /// Selects which ADC calibration bank the device applies (<c>0</c> = factory, <c>1</c> = user).
    /// </summary>
    /// <param name="bank">The calibration bank: <c>0</c> = factory, <c>1</c> = user.</param>
    /// <remarks>
    /// <b>Persisted</b> (firmware primitive <c>CONFigure:ADC:USECal</c>). The choice is written to NVM, the
    /// runtime coefficients are immediately reloaded from the selected bank, and that bank is loaded on every
    /// subsequent boot. Values other than 0 or 1 are rejected. Fire-and-forget: a device that refuses this
    /// reports nothing — use <see cref="IConfirmingDeviceAdministration.UseAdcCalibrationAsync"/> when that matters.
    /// </remarks>
    void UseAdcCalibration(int bank);

    /// <summary>
    /// Persists the device's current voltage precision setting to NVM so it survives a reboot.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over the firmware NVM primitive (<c>CONFigure:VOLTage:SAVE</c>). Pair with
    /// <see cref="LoadVoltagePrecision"/> to restore it. Fire-and-forget: a device that refuses this
    /// reports nothing — use <see cref="IConfirmingDeviceAdministration.SaveVoltagePrecisionAsync"/> when that matters.
    /// </remarks>
    void SaveVoltagePrecision();

    /// <summary>
    /// Restores the device's voltage precision setting from NVM into its runtime.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="SaveVoltagePrecision"/> (firmware primitive <c>CONFigure:VOLTage:LOAD</c>).
    /// Fire-and-forget: a device that refuses this reports nothing — use
    /// <see cref="IConfirmingDeviceAdministration.LoadVoltagePrecisionAsync"/> when that matters.
    /// </remarks>
    void LoadVoltagePrecision();
}
