namespace Daqifi.Core.Device.Internal;

/// <summary>
/// What <see cref="StreamFrameGate.AdmitFirstFrameOfSession"/> decided about a frame that arrived
/// during a streaming session the gate has no counter reference for — the first session of a
/// device instance (daqifi-nyquist-firmware #533).
/// </summary>
/// <remarks>
/// A reference-less session cannot judge its first frame on arrival, so the gate holds that frame
/// and judges it against its successor instead. The three outcomes below are the three things a
/// caller then has to do with it: nothing yet, report it dropped, or deliver it.
/// </remarks>
internal enum StreamFrameAdmission
{
    /// <summary>
    /// The frame is held until the next one arrives. Nothing is delivered, and nothing is
    /// dropped.
    /// </summary>
    Hold,

    /// <summary>
    /// The held frame is a leftover from the previous session and must be dropped and reported.
    /// The frame just offered takes its place as the held candidate, so it is not delivered
    /// either.
    /// </summary>
    DiscardHeldFrame,

    /// <summary>
    /// The held frame is genuine. Deliver it first and then the frame just offered, in that
    /// order: the held frame is the one that anchors the session clock.
    /// </summary>
    ReleaseHeldFrameThenThisOne,
}
