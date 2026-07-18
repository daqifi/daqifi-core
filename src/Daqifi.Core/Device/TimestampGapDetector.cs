using System;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Detects dropped/missing samples in a stream using the device's own hardware-timer delta
    /// between messages (<see cref="TimestampResult.SecondsBetweenMessages"/>) rather than PC
    /// arrival time. Keeping an exponential moving average (EMA) of the inter-message delta and
    /// flagging a message whose delta exceeds <see cref="GapThresholdMultiplier"/> times that
    /// average makes detection immune to TCP jitter / packet batching — only a real device-clock
    /// gap (actual data loss) trips it.
    /// </summary>
    /// <remarks>
    /// This is a per-stream primitive: one instance tracks one streaming session's cadence. The
    /// device stamps every channel in a frame with the same <c>msg_time_stamp</c>, so a gap is a
    /// property of the frame, not of an individual channel — feed it the per-message delta once per
    /// frame. Ported from the daqifi-desktop utility of the same name (issue #339) so every Core
    /// streaming consumer, not just the desktop app, gets the data-integrity signal.
    /// </remarks>
    public sealed class TimestampGapDetector
    {
        /// <summary>The default multiple of the running-average delta above which a gap is flagged.</summary>
        public const double DefaultGapThresholdMultiplier = 2.0;

        /// <summary>
        /// The default EMA smoothing factor for inter-message deltas. Lower values adapt more slowly,
        /// making detection more stable against jitter.
        /// </summary>
        public const double DefaultEmaAlpha = 0.1;

        private readonly double _thresholdMultiplier;
        private readonly double _emaAlpha;
        private double _averageDelta;
        private bool _seeded;

        /// <summary>
        /// Gets the multiple of the running-average delta above which a message is flagged as a gap.
        /// </summary>
        public double GapThresholdMultiplier => _thresholdMultiplier;

        /// <summary>
        /// Gets the EMA smoothing factor applied to inter-message deltas.
        /// </summary>
        public double EmaAlpha => _emaAlpha;

        /// <summary>
        /// Initializes a new instance of the <see cref="TimestampGapDetector"/> class.
        /// </summary>
        /// <param name="gapThresholdMultiplier">
        /// The multiple of the running-average delta above which a message is flagged as a gap.
        /// Must be greater than 1.0. Defaults to <see cref="DefaultGapThresholdMultiplier"/>.
        /// </param>
        /// <param name="emaAlpha">
        /// The EMA smoothing factor for inter-message deltas, in the range (0, 1]. Defaults to
        /// <see cref="DefaultEmaAlpha"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="gapThresholdMultiplier"/> is not greater than 1.0, or when
        /// <paramref name="emaAlpha"/> is not in the range (0, 1].
        /// </exception>
        public TimestampGapDetector(
            double gapThresholdMultiplier = DefaultGapThresholdMultiplier,
            double emaAlpha = DefaultEmaAlpha)
        {
            if (!(gapThresholdMultiplier > 1.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gapThresholdMultiplier),
                    gapThresholdMultiplier,
                    "Gap threshold multiplier must be greater than 1.0.");
            }

            if (!(emaAlpha > 0.0 && emaAlpha <= 1.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(emaAlpha),
                    emaAlpha,
                    "EMA alpha must be in the range (0, 1].");
            }

            _thresholdMultiplier = gapThresholdMultiplier;
            _emaAlpha = emaAlpha;
        }

        /// <summary>
        /// Evaluates whether the device-measured inter-message delta indicates missing samples,
        /// and folds the delta into the running average when it does not.
        /// </summary>
        /// <param name="secondsBetweenMessages">
        /// The device-clock time since the previous message, in seconds
        /// (<see cref="TimestampResult.SecondsBetweenMessages"/>). Pass <see langword="null"/> or a
        /// non-positive value for the first message of a session (no prior reference point).
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the delta significantly exceeds the running average — a real
        /// gap in the device's sample stream; otherwise <see langword="false"/>. After a gap the EMA
        /// is reset so a single large outage does not desensitise later detection.
        /// </returns>
        public bool IsGap(double? secondsBetweenMessages)
        {
            // First message, or no usable device delta — no gap possible.
            if (secondsBetweenMessages is not > 0)
            {
                return false;
            }

            var delta = secondsBetweenMessages.Value;

            if (!_seeded)
            {
                // First real delta — seed the EMA, cannot be a gap yet.
                _seeded = true;
                _averageDelta = delta;
                return false;
            }

            if (_averageDelta > 0 && delta > _thresholdMultiplier * _averageDelta)
            {
                // Reset after a detected gap so the next message re-seeds the EMA from fresh cadence.
                Reset();
                return true;
            }

            _averageDelta = (1.0 - _emaAlpha) * _averageDelta + _emaAlpha * delta;
            return false;
        }

        /// <summary>
        /// Resets the running average, returning the detector to its unseeded (start-of-session) state.
        /// </summary>
        public void Reset()
        {
            _averageDelta = 0;
            _seeded = false;
        }
    }
}
