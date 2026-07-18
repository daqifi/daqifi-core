using System.Collections.Generic;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a device that supports data streaming.
    /// </summary>
    public interface IStreamingDevice : IDevice
    {
        /// <summary>
        /// Gets or sets the streaming frequency in Hz.
        /// </summary>
        int StreamingFrequency { get; set; }

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        bool IsStreaming { get; }

        /// <summary>
        /// Starts streaming data from the device.
        /// </summary>
        void StartStreaming();

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        void StopStreaming();

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
        /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.</param>
        /// <param name="direction">The direction to apply. Must be <see cref="ChannelDirection.Input"/> or <see cref="ChannelDirection.Output"/>.</param>
        void SetDioDirection(IChannel channel, ChannelDirection direction);

        /// <summary>
        /// Sets the output state (high or low) of a digital I/O channel.
        /// </summary>
        /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.</param>
        /// <param name="value"><c>true</c> to drive the output high; <c>false</c> to drive it low.</param>
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
        /// <param name="frequencyHz">The frequency in hertz, 6-50000. Values below 6 Hz are rejected because
        /// the firmware's 16-bit period register silently wraps for them, producing a kilohertz-range output.</param>
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
        /// <param name="channelNumber">The analog output channel number. DAC channels are addressed by
        /// number; they are not part of the <c>Channels</c> collection (which holds analog inputs).</param>
        /// <param name="voltage">The output voltage, in volts. Must be a finite number.</param>
        /// <remarks>
        /// Analog output is available on NQ3 hardware only. Each call stages the level and then latches it
        /// immediately, so it is not suitable for synchronized multi-channel updates.
        /// </remarks>
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
        /// Persists the device's current ADC calibration coefficients to NVM so they survive a reboot.
        /// </summary>
        /// <remarks>
        /// A thin wrapper over the firmware NVM primitive (<c>CONFigure:ADC:SAVEcal</c>). Pair with
        /// <see cref="LoadAdcCalibration"/> to restore them.
        /// </remarks>
        void SaveAdcCalibration();

        /// <summary>
        /// Restores the device's ADC calibration coefficients from NVM into its runtime.
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="SaveAdcCalibration"/> (firmware primitive <c>CONFigure:ADC:LOADcal</c>).
        /// </remarks>
        void LoadAdcCalibration();

        /// <summary>
        /// Persists the device's current voltage precision setting to NVM so it survives a reboot.
        /// </summary>
        /// <remarks>
        /// A thin wrapper over the firmware NVM primitive (<c>CONFigure:VOLTage:SAVE</c>). Pair with
        /// <see cref="LoadVoltagePrecision"/> to restore it.
        /// </remarks>
        void SaveVoltagePrecision();

        /// <summary>
        /// Restores the device's voltage precision setting from NVM into its runtime.
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="SaveVoltagePrecision"/> (firmware primitive <c>CONFigure:VOLTage:LOAD</c>).
        /// </remarks>
        void LoadVoltagePrecision();
    }
}
