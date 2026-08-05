using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The channel-control half of <see cref="IStreamingDevice"/> — analog/digital enable, DIO
    /// direction and level, PWM, and the analog output — extracted from
    /// <see cref="DaqifiStreamingDevice"/> (#344) so the device delegates rather than hosts it.
    /// Owns the PWM frequency bookkeeping that goes with those commands.
    /// </summary>
    /// <remarks>
    /// Everything here reaches the device through <see cref="IDeviceOperationHost"/>, so each
    /// command still passes through the device's own virtual <c>Send</c> and the same channels
    /// lock the status-frame resync uses (#409).
    /// </remarks>
    internal sealed class ChannelControlOperations
    {
        /// <summary>
        /// The maximum analog channel number that can be encoded in the ADC enable bitmask.
        /// The mask is a 32-bit value (<c>1u &lt;&lt; ChannelNumber</c>), so channel numbers must be 0-31.
        /// Shared with the device's tracking of a raw ADC-enable command, which decodes the same mask.
        /// </summary>
        internal const int MaxAdcBitmaskChannel = 31;

        private readonly IDeviceOperationHost _host;

        internal ChannelControlOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="IStreamingDevice.PwmFrequencyHz" />
        internal int PwmFrequencyHz { get; private set; } = DaqifiStreamingDevice.DefaultPwmFrequencyHz;

        /// <summary>
        /// The PWM frequency actually sent to the device this connection, or <c>null</c> if none has
        /// been sent yet (also reset to <c>null</c> on disconnect). Distinct from
        /// <see cref="PwmFrequencyHz"/>, which carries a session default before anything is sent —
        /// this drives the skip-if-unchanged guard so a fresh connection always sends. See #345.
        /// </summary>
        private int? _lastSentPwmFrequencyHz;

        /// <summary>
        /// Clears the "already-sent" PWM frequency cache, so the next <see cref="SetPwmFrequency"/>
        /// sends even if the value is unchanged. Called on any transition away from Connected —
        /// after one the device's runtime PWM state is no longer trustworthy. See #345.
        /// </summary>
        internal void ResetSentPwmFrequency() => _lastSentPwmFrequencyHz = null;

        /// <inheritdoc cref="IStreamingDevice.EnableChannel" />
        internal void EnableChannel(IChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            SetChannelsEnabled(new[] { channel }, enabled: true);
        }

        /// <inheritdoc cref="IStreamingDevice.EnableChannels" />
        internal void EnableChannels(IEnumerable<IChannel> channels)
        {
            ArgumentNullException.ThrowIfNull(channels);
            SetChannelsEnabled(channels as IReadOnlyList<IChannel> ?? channels.ToList(), enabled: true);
        }

        /// <inheritdoc cref="IStreamingDevice.DisableChannel" />
        internal void DisableChannel(IChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            SetChannelsEnabled(new[] { channel }, enabled: false);
        }

        /// <inheritdoc cref="IStreamingDevice.DisableAllChannels" />
        internal void DisableAllChannels()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            (bool HasChannels, uint Mask) adcMask = default;
            (bool HasChannels, bool AnyEnabled) dioState = default;

            // Mutate and derive the outbound masks in one critical section — see the matching
            // comment in SetChannelsEnabled (#409) for why the gap matters.
            _host.WithChannelsLock(() =>
            {
                foreach (var channel in _host.SnapshotChannels())
                {
                    channel.IsEnabled = false;
                }

                adcMask = ComputeAdcEnableMask();
                dioState = ComputeDioEnableState();
            });

            // Push the cleared state for whichever channel types this device actually has.
            if (adcMask.HasChannels)
            {
                _host.Send(ScpiMessageProducer.EnableAdcChannels(adcMask.Mask.ToString(CultureInfo.InvariantCulture)));
            }

            if (dioState.HasChannels)
            {
                _host.Send(dioState.AnyEnabled
                    ? ScpiMessageProducer.EnableDioPorts()
                    : ScpiMessageProducer.DisableDioPorts());
            }
        }

        /// <inheritdoc cref="IStreamingDevice.SetDioDirection" />
        internal void SetDioDirection(IChannel channel, ChannelDirection direction)
        {
            // Argument validation precedes the connection (state) check so misuse surfaces
            // the same exception type regardless of connection state.
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("Direction can only be set on digital channels.", nameof(channel));
            }

            if (direction != ChannelDirection.Input && direction != ChannelDirection.Output)
            {
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be Input or Output.");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            channel.Direction = direction;
            _host.Send(ScpiMessageProducer.SetDioPortDirection(
                channel.ChannelNumber,
                direction == ChannelDirection.Output ? 1 : 0));
        }

        /// <inheritdoc cref="IStreamingDevice.SetDioValue" />
        internal void SetDioValue(IChannel channel, bool value)
        {
            ArgumentNullException.ThrowIfNull(channel);

            // Gate on Type (matching SetDioDirection) rather than the IDigitalChannel interface,
            // so both DIO methods accept the same set of channels. The SCPI command only needs
            // the channel number; OutputValue mirroring is best-effort local bookkeeping.
            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("A digital output value can only be set on digital channels.", nameof(channel));
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.OutputValue = value;
            }

            _host.Send(ScpiMessageProducer.SetDioPortState(channel.ChannelNumber, value ? 1 : 0));
        }

        /// <inheritdoc cref="IStreamingDevice.SetPwmEnabled" />
        internal void SetPwmEnabled(IChannel channel, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("PWM can only be controlled on digital channels.", nameof(channel));
            }

            // Enabling PWM on a non-capable channel must be blocked here: the firmware flags the
            // channel PWM-active before its capability check fails and never rolls that back,
            // leaving the channel dead to digital writes. Disabling is that state's only recovery
            // command, so it is accepted on any digital channel.
            if (enabled && channel is not IDigitalChannel { IsPwmCapable: true })
            {
                throw new ArgumentException(
                    $"Channel {channel.ChannelNumber} does not support PWM. PWM-capable channels: {PwmCapableChannelList}.",
                    nameof(channel));
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.IsPwmEnabled = enabled;
                if (!enabled)
                {
                    // Disabling PWM leaves the pin high-impedance and the firmware zeroes its
                    // stored output value; mirror that so local state doesn't claim a driven level.
                    // Direction is intentionally left as-is: the firmware keeps the channel's
                    // stored direction and re-applies it (resuming driving) on the next state or
                    // direction write, or on the next streaming tick — verified on hardware.
                    digitalChannel.OutputValue = false;
                }
            }

            _host.Send(ScpiMessageProducer.SetPwmChannelEnabled(channel.ChannelNumber, enabled));
        }

        /// <inheritdoc cref="IStreamingDevice.SetPwmDutyCycle" />
        internal void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent)
        {
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("PWM can only be controlled on digital channels.", nameof(channel));
            }

            if (channel is not IDigitalChannel { IsPwmCapable: true })
            {
                throw new ArgumentException(
                    $"Channel {channel.ChannelNumber} does not support PWM. PWM-capable channels: {PwmCapableChannelList}.",
                    nameof(channel));
            }

            // Duty 0 is rejected rather than forwarded: the firmware stores it but never writes
            // the compare register, so the output keeps toggling at the previous duty while the
            // stored value claims 0. Stopping the output is SetPwmEnabled(channel, false).
            if (dutyCyclePercent is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dutyCyclePercent), dutyCyclePercent,
                    "Duty cycle must be 1-100 percent. To stop the output, use SetPwmEnabled(channel, false).");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.PwmDutyCyclePercent = dutyCyclePercent;
            }

            _host.Send(ScpiMessageProducer.SetPwmChannelDutyCycle(channel.ChannelNumber, dutyCyclePercent));
        }

        /// <inheritdoc cref="IStreamingDevice.SetPwmFrequency" />
        internal void SetPwmFrequency(int frequencyHz)
        {
            if (frequencyHz is < DaqifiStreamingDevice.MinPwmFrequencyHz or > DaqifiStreamingDevice.MaxPwmFrequencyHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyHz), frequencyHz,
                    $"PWM frequency must be {DaqifiStreamingDevice.MinPwmFrequencyHz}-{DaqifiStreamingDevice.MaxPwmFrequencyHz} Hz.");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Skip the redundant round-trip when the device already has this frequency (from a
            // send earlier this connection). The cache is cleared on disconnect so a fresh
            // connection always sends. PwmFrequencyHz still reflects the commanded value. See #345.
            if (frequencyHz == _lastSentPwmFrequencyHz)
            {
                return;
            }

            // The SCPI command is addressed to a channel, but the firmware drives all PWM from
            // one shared timer and applies the frequency to every channel. Channel 0 is used as
            // the address because it is PWM-capable on all supported hardware.
            _host.Send(ScpiMessageProducer.SetPwmChannelFrequency(0, frequencyHz));
            _lastSentPwmFrequencyHz = frequencyHz;
            PwmFrequencyHz = frequencyHz;
        }

        /// <summary>
        /// Comma-separated PWM-capable channel numbers for error messages, derived from this
        /// device's channel collection.
        /// </summary>
        private string PwmCapableChannelList
        {
            get
            {
                var capable = new List<int>();
                foreach (var ch in _host.SnapshotChannels())
                {
                    if (ch is IDigitalChannel { IsPwmCapable: true })
                    {
                        capable.Add(ch.ChannelNumber);
                    }
                }
                capable.Sort();
                return capable.Count > 0 ? string.Join(", ", capable) : "none on this device";
            }
        }

        /// <inheritdoc cref="IStreamingDevice.SetAnalogOutput" />
        internal void SetAnalogOutput(int channelNumber, double voltage)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Analog-output (DAC) channels are addressed by number; they are not part of the
            // populated Channels collection (PopulateChannelsFromStatus creates analog *input*
            // channels only). Stage the level, then latch it.
            _host.Send(ScpiMessageProducer.SetAnalogOutputVoltage(channelNumber, voltage));
            _host.Send(ScpiMessageProducer.UpdateDacOutputs);
        }

        /// <summary>
        /// Sets the enabled state for a set of channels, then sends one device command per affected
        /// channel type (the ADC enable bitmask for analog, the global DIO enable for digital).
        /// Validation runs before any mutation so an invalid entry leaves device state untouched.
        /// </summary>
        private void SetChannelsEnabled(IReadOnlyList<IChannel> channels, bool enabled)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Validate everything up front so a bad entry can't leave a partially-applied state.
            foreach (var channel in channels)
            {
                if (channel is null)
                {
                    throw new ArgumentException("The channel collection contains a null entry.", nameof(channels));
                }

                EnsureChannelBelongs(channel);
            }

            var touchedAnalog = false;
            var touchedDigital = false;
            var adcMask = (HasChannels: false, Mask: 0u);
            var dioState = (HasChannels: false, AnyEnabled: false);

            // Mutate IsEnabled and derive the outbound masks from it in one critical section, so
            // a status frame resyncing analog IsEnabled from the device (#409) on the consumer
            // thread cannot interleave between the mutation and the read that computes the mask —
            // which would otherwise send a mask reflecting a value the status frame is about to
            // overwrite, silently failing to apply the requested enable/disable on the device.
            _host.WithChannelsLock(() =>
            {
                foreach (var channel in channels)
                {
                    channel.IsEnabled = enabled;

                    if (channel.Type == ChannelType.Analog)
                    {
                        touchedAnalog = true;
                    }
                    else if (channel.Type == ChannelType.Digital)
                    {
                        touchedDigital = true;
                    }
                }

                if (touchedAnalog)
                {
                    adcMask = ComputeAdcEnableMask();
                }

                if (touchedDigital)
                {
                    dioState = ComputeDioEnableState();
                }
            });

            if (touchedAnalog && adcMask.HasChannels)
            {
                _host.Send(ScpiMessageProducer.EnableAdcChannels(adcMask.Mask.ToString(CultureInfo.InvariantCulture)));
            }

            if (touchedDigital && dioState.HasChannels)
            {
                _host.Send(dioState.AnyEnabled
                    ? ScpiMessageProducer.EnableDioPorts()
                    : ScpiMessageProducer.DisableDioPorts());
            }
        }

        /// <summary>
        /// Computes the ADC enable bitmask over all currently-enabled analog channels. Must be
        /// called under <see cref="IDeviceOperationHost.WithChannelsLock"/> alongside any IsEnabled
        /// mutation it should reflect (#409) — see <see cref="SetChannelsEnabled"/>.
        /// </summary>
        private (bool HasChannels, uint Mask) ComputeAdcEnableMask()
        {
            uint mask = 0;
            var hasAnalogChannels = false;

            foreach (var channel in _host.SnapshotChannels())
            {
                if (channel.Type != ChannelType.Analog)
                {
                    continue;
                }

                hasAnalogChannels = true;

                if (!channel.IsEnabled)
                {
                    continue;
                }

                if (channel.ChannelNumber > MaxAdcBitmaskChannel)
                {
                    throw new InvalidOperationException(
                        $"Analog channel number {channel.ChannelNumber} exceeds the maximum ({MaxAdcBitmaskChannel}) that can be encoded in the ADC enable bitmask.");
                }

                mask |= 1u << channel.ChannelNumber;
            }

            return (hasAnalogChannels, mask);
        }

        /// <summary>
        /// Computes whether any digital channel is enabled. Must be called under
        /// <see cref="IDeviceOperationHost.WithChannelsLock"/> alongside any IsEnabled mutation it
        /// should reflect — see <see cref="SetChannelsEnabled"/>.
        /// </summary>
        private (bool HasChannels, bool AnyEnabled) ComputeDioEnableState()
        {
            var hasDigitalChannels = false;
            var anyEnabled = false;

            foreach (var channel in _host.SnapshotChannels())
            {
                if (channel.Type != ChannelType.Digital)
                {
                    continue;
                }

                hasDigitalChannels = true;

                if (channel.IsEnabled)
                {
                    anyEnabled = true;
                }
            }

            return (hasDigitalChannels, anyEnabled);
        }

        /// <summary>
        /// Throws when the supplied channel is not part of this device's populated channel collection,
        /// which would mean mutating it could not affect the device-level enable state.
        /// </summary>
        private void EnsureChannelBelongs(IChannel channel)
        {
            if (!_host.SnapshotChannels().Contains(channel))
            {
                throw new ArgumentException("The specified channel does not belong to this device.", nameof(channel));
            }
        }
    }
}
