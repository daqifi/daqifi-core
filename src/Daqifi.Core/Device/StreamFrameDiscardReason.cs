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
        /// <para>
        /// Firmware up to and including 3.7.2 can emit a leading frame at stream start whose analog
        /// payload is short (daqifi-core #351, daqifi-nyquist-firmware #707). Consumers that infer
        /// channel width from the first sample would silently truncate the whole capture, so the
        /// malformed analog payload is withheld. Any digital payload in the same frame is still
        /// delivered, and the frame's timestamp still anchors the session clock.
        /// </para>
        /// <para>
        /// How short it is depends on the enabled channel <em>mask</em>, not on how many channels
        /// are enabled, so there is no fixed shape to match against. Measured on a bench Nq1 running
        /// 3.7.2 (daqifi-core #544), the leading frame carried 1 value for mask <c>{0,1,2}</c> but 2
        /// for <c>{0,1,10}</c> — same channel count, different width — 2 for <c>{0..7}</c>, 5 for
        /// <c>{8..15}</c>, and 7 for all sixteen channels, while masks <c>{15}</c> and
        /// <c>{14,15}</c> produced no malformed frame at all. The width is deterministic per mask
        /// and independent of sample rate. A one-value frame is therefore the common case rather
        /// than the shape of the defect, and "carried fewer values than there are enabled channels"
        /// is the only test that holds across masks.
        /// </para>
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
