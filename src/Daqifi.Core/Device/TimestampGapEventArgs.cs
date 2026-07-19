using System;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Carries information about a detected gap in the device's sample stream, raised by
    /// <see cref="DaqifiStreamingDevice.GapDetected"/> when the device-clock delta between two
    /// consecutive frames indicates missing samples.
    /// </summary>
    public sealed class TimestampGapEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the reconstructed host timestamp of the first frame received after the gap.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Gets the device-clock time, in seconds, between the frame before the gap and the frame
        /// that triggered detection — i.e. the duration of the outage.
        /// </summary>
        public double SecondsSincePreviousMessage { get; }

        /// <summary>
        /// Gets the raw device tick counter value of the frame that triggered detection.
        /// </summary>
        public uint DeviceTimestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TimestampGapEventArgs"/> class.
        /// </summary>
        /// <param name="timestamp">The host timestamp of the first frame after the gap.</param>
        /// <param name="secondsSincePreviousMessage">The device-clock outage duration, in seconds.</param>
        /// <param name="deviceTimestamp">The raw device tick counter value of the triggering frame.</param>
        public TimestampGapEventArgs(DateTime timestamp, double secondsSincePreviousMessage, uint deviceTimestamp)
        {
            Timestamp = timestamp;
            SecondsSincePreviousMessage = secondsSincePreviousMessage;
            DeviceTimestamp = deviceTimestamp;
        }
    }
}
