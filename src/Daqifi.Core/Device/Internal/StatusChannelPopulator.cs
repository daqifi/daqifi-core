using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// Maps a device status frame's channel description onto <see cref="IChannel"/> instances,
    /// extracted from <see cref="DaqifiDevice"/> so the device delegates rather than hosts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pure mapping half of <see cref="DaqifiDevice.PopulateChannelsFromStatus"/>:
    /// protobuf fields in, channel instances out, plus the sanitization of the device-reported
    /// calibration values. The device keeps everything that is not mapping — the channels lock,
    /// the list swap, the timestamp-frequency update, and the <c>ChannelsPopulated</c> event —
    /// because those are device state and device notification, not translation.
    /// </para>
    /// <para>
    /// Holds no state of its own, so nothing here needs resetting between populations and it is
    /// safe to call on whatever thread the device already holds its channels lock on.
    /// </para>
    /// </remarks>
    internal sealed class StatusChannelPopulator
    {
        /// <summary>
        /// Bitmask of digital channels whose hardware supports PWM output (bit n = channel n).
        /// Channels 0, 3, 4, 5, 6 and 7 route to output-compare modules; the mask comes from the
        /// firmware's board configuration and is identical across Nyquist variants.
        /// </summary>
        private const int PwmCapableChannelMask = 0x00F9;

        private readonly ILogger _logger;
        private readonly Func<string> _deviceName;

        /// <summary>
        /// Creates a populator that logs against the owning device.
        /// </summary>
        /// <param name="logger">The device's logger; warnings about implausible device-reported values go here.</param>
        /// <param name="deviceName">
        /// Reads the owning device's current name for those warnings. A delegate rather than a
        /// captured string because the name can change during the device's lifetime, and a warning
        /// naming the wrong device is worse than one naming none.
        /// </param>
        internal StatusChannelPopulator(ILogger logger, Func<string> deviceName)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
        }

        /// <summary>
        /// Translates a status message's channel description into channel instances, appended to
        /// <paramref name="destination"/> in device order (analog first, then digital).
        /// </summary>
        /// <param name="message">The protobuf status message containing channel configuration.</param>
        /// <param name="existing">
        /// The channels from the prior population. Any whose identity (type, number) still appears
        /// in <paramref name="message"/> is updated in place and re-used, so consumer-held
        /// <see cref="IChannel"/> references — and the configuration on them (direction/output/PWM
        /// state) — survive a routine status re-population untouched. <c>IsEnabled</c> is the
        /// exception: analog channels resync it from the device-reported enabled mask (field 22)
        /// whenever the device sends one, so Core's view cannot silently drift from the device's
        /// (#409).
        /// </param>
        /// <param name="destination">The list to append the resulting channel instances to, in order.</param>
        /// <returns>How many analog and digital channels were populated.</returns>
        internal (int AnalogCount, int DigitalCount) Populate(
            DaqifiOutMessage message,
            IReadOnlyList<IChannel> existing,
            List<IChannel> destination)
        {
            var analogCount = 0;
            var digitalCount = 0;

            // Index existing channels by identity (type, number) for the in-place reuse described
            // on the parameter above.
            var existingByKey = new Dictionary<(ChannelType, int), IChannel>();
            foreach (var channel in existing)
            {
                existingByKey[(channel.Type, channel.ChannelNumber)] = channel;
            }

            // Populate analog input channels
            if (message.AnalogInPortNum > 0)
            {
                analogCount = PopulateAnalogChannels(message, existingByKey, destination);
            }

            // Populate digital channels
            if (message.DigitalPortNum > 0)
            {
                digitalCount = PopulateDigitalChannels(message, existingByKey, destination);
            }

            return (analogCount, digitalCount);
        }

        /// <summary>
        /// Populates analog channels from the protobuf message, updating existing channel
        /// instances in place where their identity (type, number) is unchanged.
        /// </summary>
        /// <param name="message">The protobuf message containing analog channel data.</param>
        /// <param name="existingByKey">Existing channels from the prior population, keyed by (type, number).</param>
        /// <param name="destination">The list to append the resulting channel instances to, in order.</param>
        /// <returns>The number of analog channels populated.</returns>
        private int PopulateAnalogChannels(DaqifiOutMessage message, Dictionary<(ChannelType, int), IChannel> existingByKey, List<IChannel> destination)
        {
            var analogInPortRanges = message.AnalogInPortRange;
            var analogInCalibrationBValues = message.AnalogInCalB;
            var analogInCalibrationMValues = message.AnalogInCalM;
            var analogInInternalScaleMValues = message.AnalogInIntScaleM;
            var analogInResolution = message.AnalogInRes;
            var analogInPortEnabled = message.AnalogInPortEnabled;

            // Firmware before v3.5.0 never populates this field, so an empty byte string is
            // ambiguous between "no channels enabled" and "not reported". Only trust it as the
            // source of truth for IsEnabled when the device actually sent something.
            var enabledIsReported = analogInPortEnabled.Length > 0;

            var count = (int)message.AnalogInPortNum;

            // Treat both a missing (0) and a physically-implausible out-of-range resolution as
            // "assumed": the AnalogChannel constructor/setters now reject anything outside
            // [MinResolution, MaxResolution], so passing a corrupt non-zero value straight through
            // would throw and abort channel population mid-stream. Fall back to a safe default and
            // log instead, so a corrupted status frame can neither crash population nor silently
            // corrupt every scaled sample on the reuse path (UpdateScalingFromStatus below).
            var resolutionIsAssumed = analogInResolution is < AnalogChannel.MinResolution or > AnalogChannel.MaxResolution;
            var resolution = resolutionIsAssumed ? 65535u : analogInResolution;

            if (resolutionIsAssumed && count > 0)
            {
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported no usable ADC resolution (analog_in_res={Resolution}) for {ChannelCount} analog channel(s); assuming {AssumedResolution}. Scaled samples on this device may be systematically wrong.", _deviceName(), analogInResolution, count, resolution));
            }

            for (var i = 0; i < count; i++)
            {
                var calibrationB = GetWithDefault(analogInCalibrationBValues, i, 0.0f);
                var calibrationM = GetWithDefault(analogInCalibrationMValues, i, 1.0f);
                var internalScaleM = GetWithDefault(analogInInternalScaleMValues, i, 1.0f);
                var portRange = GetWithDefault(analogInPortRanges, i, 1.0f);

                // A corrupted device response can carry NaN/Infinity or physically nonsensical
                // scaling coefficients. Feeding those into AnalogChannel would either throw from its
                // validating setters (killing channel population mid-stream) or silently propagate
                // garbage into every scaled sample. Fall back to safe defaults and log instead —
                // mirroring the analog_in_res=0 handling above.
                calibrationB = (float)SanitizeScalingValue(calibrationB, 0.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: false, i, nameof(calibrationB));
                calibrationM = (float)SanitizeScalingValue(calibrationM, 1.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: true, i, nameof(calibrationM));
                internalScaleM = (float)SanitizeScalingValue(internalScaleM, 1.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: true, i, nameof(internalScaleM));
                portRange = (float)SanitizePortRange(portRange, i);

                if (existingByKey.TryGetValue((ChannelType.Analog, i), out var existing) && existing is AnalogChannel existingAnalog)
                {
                    existingAnalog.UpdateScalingFromStatus(resolution, calibrationB, calibrationM, internalScaleM, portRange, resolutionIsAssumed);
                    if (enabledIsReported)
                    {
                        existingAnalog.IsEnabled = IsChannelBitSet(analogInPortEnabled, i);
                    }
                    destination.Add(existingAnalog);
                    continue;
                }

                var channel = new AnalogChannel(i, resolution, resolutionIsAssumed)
                {
                    Name = $"AI{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = enabledIsReported && IsChannelBitSet(analogInPortEnabled, i),
                    CalibrationB = calibrationB,
                    CalibrationM = calibrationM,
                    InternalScaleM = internalScaleM,
                    PortRange = portRange
                };

                destination.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Clamps a device-reported calibration/scale coefficient to a value <see cref="AnalogChannel"/>
        /// will accept, substituting <paramref name="fallback"/> and logging when the reported value is
        /// non-finite, out of magnitude range, or (when <paramref name="requireNonZero"/>) zero.
        /// </summary>
        private double SanitizeScalingValue(double value, double fallback, double maxMagnitude, bool requireNonZero, int channelIndex, string fieldName)
        {
            var invalid = !double.IsFinite(value)
                || Math.Abs(value) > maxMagnitude
                || (requireNonZero && value == 0.0);

            if (invalid)
            {
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported invalid {FieldName}={Value} for analog channel {ChannelIndex}; substituting {Fallback}. Scaled samples on this channel may be affected.", _deviceName(), fieldName, value, channelIndex, fallback));
                return fallback;
            }

            return value;
        }

        /// <summary>
        /// Clamps a device-reported port range to a value <see cref="AnalogChannel"/> will accept,
        /// substituting the 1.0 default and logging when the reported value is non-finite, non-positive,
        /// or beyond <see cref="AnalogChannel.MaxPortRangeVolts"/>.
        /// </summary>
        private double SanitizePortRange(double value, int channelIndex)
        {
            if (!double.IsFinite(value) || value <= 0.0 || value > AnalogChannel.MaxPortRangeVolts)
            {
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported invalid portRange={Value} for analog channel {ChannelIndex}; substituting 1.0. Scaled samples on this channel may be affected.", _deviceName(), value, channelIndex));
                return 1.0;
            }

            return value;
        }

        /// <summary>
        /// Populates digital channels from the protobuf message, updating existing channel
        /// instances in place where their identity (type, number) is unchanged.
        /// </summary>
        /// <param name="message">The protobuf message containing digital channel data.</param>
        /// <param name="existingByKey">Existing channels from the prior population, keyed by (type, number).</param>
        /// <param name="destination">The list to append the resulting channel instances to, in order.</param>
        /// <returns>The number of digital channels populated.</returns>
        private static int PopulateDigitalChannels(DaqifiOutMessage message, Dictionary<(ChannelType, int), IChannel> existingByKey, List<IChannel> destination)
        {
            var count = (int)message.DigitalPortNum;

            for (var i = 0; i < count; i++)
            {
                var isPwmCapable = i < 32 && (PwmCapableChannelMask & (1 << i)) != 0;

                if (existingByKey.TryGetValue((ChannelType.Digital, i), out var existing) && existing is DigitalChannel existingDigital)
                {
                    existingDigital.IsPwmCapable = isPwmCapable;
                    destination.Add(existingDigital);
                    continue;
                }

                var channel = new DigitalChannel(i, isPwmCapable)
                {
                    Name = $"DIO{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = false
                };

                destination.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Reads bit <paramref name="channelNumber"/> from a device-reported per-channel enable
        /// bitmask (analog_in_port_enabled, field 22 — confirmed bit-packed on the bench: 2 bytes
        /// for 16 channels, little-endian, bit <c>n</c> = channel <c>n</c> — the same layout Core
        /// sends outbound via <see cref="Communication.Producers.ScpiMessageProducer.EnableAdcChannels"/>).
        /// Returns false when the channel number falls outside the bytes actually sent.
        /// </summary>
        private static bool IsChannelBitSet(Google.Protobuf.ByteString mask, int channelNumber)
        {
            var byteIndex = channelNumber / 8;
            return byteIndex < mask.Length && (mask[byteIndex] & (1 << (channelNumber % 8))) != 0;
        }

        /// <summary>
        /// Gets a value from a list with a default fallback if the index is out of range.
        /// </summary>
        /// <param name="list">The list to get the value from.</param>
        /// <param name="index">The index to retrieve.</param>
        /// <param name="defaultValue">The default value if the index is out of range.</param>
        /// <returns>The value at the index or the default value.</returns>
        private static T GetWithDefault<T>(IList<T> list, int index, T defaultValue)
        {
            if (list.Count > index)
            {
                return list[index];
            }
            return defaultValue;
        }

        /// <summary>
        /// Runs a logging call, swallowing anything a consumer-supplied <see cref="ILogger"/>
        /// throws. Mirrors <c>DaqifiDevice.SafeLog</c>, which is private to that class.
        /// </summary>
        /// <remarks>
        /// A logger that throws must not abort channel population: the warnings guarded here are
        /// emitted precisely when the device reported something implausible, so a faulting logger
        /// would turn a recoverable bad status frame into a failed population.
        /// </remarks>
        private static void SafeLog(Action logAction)
        {
            try
            {
                logAction();
            }
            catch
            {
                // A logger that throws is not permitted to affect device operation.
            }
        }
    }
}
