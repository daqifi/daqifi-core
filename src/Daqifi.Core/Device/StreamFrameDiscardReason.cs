namespace Daqifi.Core.Device
{
    /// <summary>
    /// Why <see cref="DaqifiStreamingDevice"/> withheld a stream frame from its consumers, reported
    /// by <see cref="DaqifiStreamingDevice.StreamFrameDiscarded"/>.
    /// </summary>
    /// <remarks>
    /// Every value here describes a device-side defect that host-side protection papers over, so a
    /// discard is never a Core bug report — it is Core telling you the wire carried something the
    /// device should not have sent.
    /// </remarks>
    public enum StreamFrameDiscardReason
    {
        /// <summary>
        /// The frame carried fewer analog values than the number of enabled analog channels.
        /// </summary>
        /// <remarks>
        /// Firmware up to and including 3.7.2 emits a leading frame at stream start whose analog
        /// payload holds a single value regardless of the enabled channel mask (daqifi-core #351,
        /// daqifi-nyquist-firmware #707). Consumers that infer channel width from the first sample
        /// would silently truncate the whole capture, so the malformed analog payload is withheld.
        /// Any digital payload in the same frame is still delivered, and the frame's timestamp still
        /// anchors the session clock.
        /// </remarks>
        PartialAnalogFrame,

        /// <summary>
        /// The frame belonged to the previous streaming session.
        /// </summary>
        /// <remarks>
        /// The device latches the final frame of a stopped session in its transmit path and emits it
        /// as the first frame of the next one (daqifi-nyquist-firmware #533). Its device-tick counter
        /// sits about one sample period after the last frame of the previous session rather than at
        /// the new session's start, so it would anchor the session clock to a time that never
        /// happened.
        /// </remarks>
        StaleLeftoverFrame,
    }
}
