using System;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Carries information about a stream frame that <see cref="DaqifiStreamingDevice"/> withheld
    /// from its consumers because the device should not have sent it.
    /// </summary>
    /// <remarks>
    /// This exists so a discard is never invisible. Silently dropping a frame is the right thing to
    /// do with a malformed one, but a consumer counting samples, watching for dropouts, or
    /// reconciling against the device's own frame count needs to be able to tell the difference
    /// between "Core suppressed a bad frame" and "the device sent nothing".
    /// </remarks>
    public sealed class StreamFrameDiscardedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the reason the frame was withheld.
        /// </summary>
        public StreamFrameDiscardReason Reason { get; }

        /// <summary>
        /// Gets the raw device tick counter value carried by the discarded frame.
        /// </summary>
        public uint DeviceTimestamp { get; }

        /// <summary>
        /// Gets the number of analog values the discarded frame carried.
        /// </summary>
        public int AnalogValueCount { get; }

        /// <summary>
        /// Gets the number of analog channels enabled when the frame arrived — the number of values
        /// a well-formed frame would have carried.
        /// </summary>
        public int EnabledAnalogChannelCount { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamFrameDiscardedEventArgs"/> class.
        /// </summary>
        /// <param name="reason">Why the frame was withheld.</param>
        /// <param name="deviceTimestamp">The discarded frame's raw device tick counter value.</param>
        /// <param name="analogValueCount">How many analog values the discarded frame carried.</param>
        /// <param name="enabledAnalogChannelCount">How many analog channels were enabled at the time.</param>
        public StreamFrameDiscardedEventArgs(
            StreamFrameDiscardReason reason,
            uint deviceTimestamp,
            int analogValueCount,
            int enabledAnalogChannelCount)
        {
            Reason = reason;
            DeviceTimestamp = deviceTimestamp;
            AnalogValueCount = analogValueCount;
            EnabledAnalogChannelCount = enabledAnalogChannelCount;
        }
    }
}
