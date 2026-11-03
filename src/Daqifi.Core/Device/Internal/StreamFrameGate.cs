using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Keeps the frame the device latched from the previous streaming session out of the current one
/// (daqifi-nyquist-firmware #533).
/// </summary>
/// <remarks>
/// <para>
/// The device holds the final frame of a stopped session in its transmit path and emits it as
/// the first frame of the next session. Its free-running tick counter is never reset, so that
/// frame arrives carrying a counter value from the session before — and left alone it prepends a
/// stale sample to the capture and anchors the new session's clock to a time that never
/// happened.
/// </para>
/// <para>
/// The counter is what tells the two apart. A latched frame sits about one sample period past
/// the last counter value seen before the session began; a genuine first frame is offset by the
/// whole stop-to-start gap. Comparisons use modular <see cref="uint"/> subtraction, so they stay
/// correct across the counter's wrap (about 86 s at the 50 MHz default tick rate).
/// </para>
/// <para>
/// The first session of a device instance has no such counter value: the device's latched frame
/// survives a disconnect, but a freshly connected instance has seen nothing to recognize it
/// against. That session is guarded the other way round, by
/// <see cref="AdmitFirstFrameOfSession"/> — the first frame is held until the next one arrives,
/// and the gap between the two says which of them started the session. A held frame whose
/// successor is a whole stop-to-start gap away is the leftover; one whose successor is a sample
/// period away is genuine. The cost is one sample period of latency on the first sample of the
/// first session, so the caller arms it only for the firmware that needs it (issue #773).
/// </para>
/// <para>
/// This type is not thread-safe by itself. It is driven from the message-consumer thread that
/// decodes frames and re-armed from the caller thread that starts a session — the same
/// arrangement, and the same trade, as the timestamp processor and gap detector it sits beside.
/// </para>
/// </remarks>
internal sealed class StreamFrameGate
{
    /// <summary>
    /// How far past the session-start counter, measured in sample periods, a frame can sit and
    /// still be treated as belonging to the previous session.
    /// </summary>
    /// <remarks>
    /// The latched frame sits one sample period past the previous session's last frame, so the
    /// window has to clear one period with margin. Scaling it by the sample period rather than
    /// fixing it in seconds is what keeps a quick stop/start from being mistaken for a leftover:
    /// at 20 Hz the window is 125 ms, not the 2.5 s a fixed window would impose.
    /// </remarks>
    internal const double LeftoverWindowSamplePeriods = 2.5;

    /// <summary>
    /// Rate assumed when the streaming frequency is unknown, in Hz. The device's slowest rate,
    /// which makes the window its widest — the conservative choice when there is nothing better
    /// to go on.
    /// </summary>
    internal const int FallbackStreamingFrequencyHz = 1;

    /// <summary>
    /// Hard cap on discards per session, so a device whose counter behaves in a way this gate
    /// did not anticipate loses a bounded prefix of one session rather than the whole stream.
    /// </summary>
    /// <remarks>
    /// The window arithmetic already bounds discards on its own: comparisons are made against a
    /// counter reference fixed at session start, so successive frames march steadily out of the
    /// window and at most <see cref="LeftoverWindowSamplePeriods"/> of them can fall inside it.
    /// Measuring against the last frame seen instead would let a quick restart cascade — every
    /// genuine frame sitting one period from its discarded predecessor, and discarded in turn —
    /// which is the trap this cap exists to backstop.
    /// </remarks>
    internal const int MaxDiscardedFrames = 5;

    private uint _lastSeenDeviceTimestamp;
    private bool _hasDeviceTimestampReference;

    private uint _sessionStartReference;
    private bool _checkForLeftoverFrames;
    private int _discardedFrameCount;
    private double _leftoverWindowTicks;

    /// <summary>
    /// The first frame of a session begun without a counter reference, withheld until its
    /// successor can vouch for it. Null whenever nothing is held.
    /// </summary>
    private DaqifiOutMessage? _heldFirstFrame;

    /// <summary>
    /// Backing field for <see cref="IsAwaitingFirstFramePair"/>.
    /// </summary>
    private bool _awaitingFirstFramePair;

    /// <summary>
    /// Gets a value indicating whether the gate still has something to decide. False for the
    /// overwhelming majority of frames, which lets the caller skip evaluation entirely.
    /// </summary>
    /// <remarks>
    /// This is the reference-based path only, and it is mutually exclusive with
    /// <see cref="IsAwaitingFirstFramePair"/>: a session has a counter reference or it has not.
    /// </remarks>
    public bool IsValidating => _checkForLeftoverFrames;

    /// <summary>
    /// Gets a value indicating whether frames must go to
    /// <see cref="AdmitFirstFrameOfSession"/> rather than be evaluated one at a time, because
    /// this session began with no counter reference to judge its first frame against.
    /// </summary>
    public bool IsAwaitingFirstFramePair => _awaitingFirstFramePair;

    /// <summary>
    /// Gets the number of frames discarded since the current session began.
    /// </summary>
    public int DiscardedFrameCount => _discardedFrameCount;

    /// <summary>
    /// Re-arms the gate for a streaming session that is about to begin.
    /// </summary>
    /// <param name="timestampFrequency">
    /// The device's tick frequency in Hz; zero falls back to
    /// <see cref="TimestampProcessor.DefaultTimestampFrequency"/>.
    /// </param>
    /// <param name="streamingFrequencyHz">
    /// The rate the session is starting at; zero or negative falls back to
    /// <see cref="FallbackStreamingFrequencyHz"/>.
    /// </param>
    /// <param name="validateFirstFramesWithoutReference">
    /// Whether to guard a session that has no counter reference by holding its first frame (see
    /// <see cref="AdmitFirstFrameOfSession"/>). Off by default because it delays the first
    /// sample by one sample period; the caller turns it on for the firmware that latches frames
    /// across sessions. Has no effect once the instance has a counter reference, which every
    /// session after the first does.
    /// </param>
    public void BeginSession(
        uint timestampFrequency,
        int streamingFrequencyHz,
        bool validateFirstFramesWithoutReference = false)
    {
        var ticksPerSecond = timestampFrequency != 0
            ? timestampFrequency
            : TimestampProcessor.DefaultTimestampFrequency;
        var rate = streamingFrequencyHz > 0 ? streamingFrequencyHz : FallbackStreamingFrequencyHz;
        _leftoverWindowTicks = LeftoverWindowSamplePeriods * (ticksPerSecond / (double)rate);

        _discardedFrameCount = 0;
        _sessionStartReference = _lastSeenDeviceTimestamp;
        _checkForLeftoverFrames = _hasDeviceTimestampReference;

        // A frame still held when a session ends belongs to that session and is dropped with it.
        _heldFirstFrame = null;
        _awaitingFirstFramePair = validateFirstFramesWithoutReference && !_hasDeviceTimestampReference;
    }

    /// <summary>
    /// Records a frame's counter value without evaluating it. Used for frames that arrive
    /// outside a streaming session and for frames that arrive once the gate is satisfied.
    /// </summary>
    /// <remarks>
    /// Frames that arrive while no session is running matter as much as any other: the device
    /// can emit a final frame after a stop command lands, and the frame latched for the next
    /// session follows <i>that</i> one by a sample period. Leaving it out of the reference would
    /// aim the next session's window at the wrong counter value.
    /// </remarks>
    /// <param name="deviceTimestamp">The frame's raw 32-bit counter value.</param>
    public void TrackFrame(uint deviceTimestamp)
    {
        _lastSeenDeviceTimestamp = deviceTimestamp;
        _hasDeviceTimestampReference = true;
    }

    /// <summary>
    /// Decides whether a frame that arrived while <see cref="IsValidating"/> is true belongs to
    /// the previous streaming session.
    /// </summary>
    /// <param name="message">The frame that just arrived.</param>
    /// <returns>
    /// <c>true</c> when the frame is a leftover and must be dropped; <c>false</c> when it is
    /// genuine, in which case the gate steps out of the way for the rest of the session.
    /// </returns>
    public bool IsLeftoverFromPreviousSession(DaqifiOutMessage message)
    {
        var deviceTimestamp = message.MsgTimeStamp;

        if (_discardedFrameCount < MaxDiscardedFrames
            && TicksSince(_sessionStartReference, deviceTimestamp) < _leftoverWindowTicks)
        {
            _discardedFrameCount++;
            TrackFrame(deviceTimestamp);
            return true;
        }

        _checkForLeftoverFrames = false;
        TrackFrame(deviceTimestamp);
        return false;
    }

    /// <summary>
    /// Decides what to do with a frame that arrived while <see cref="IsAwaitingFirstFramePair"/>
    /// is true — a session with no counter reference of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame cannot be judged on arrival, so it is held and judged against the next frame.
    /// The reference-based path asks whether a frame sits within
    /// <see cref="LeftoverWindowSamplePeriods"/> of the counter value the session inherited; this
    /// one asks the same question from the other side, of the held frame and its successor. Two
    /// frames a sample period apart are both from this session. A held frame whose successor is a
    /// whole stop-to-start gap away was latched before the session began.
    /// </para>
    /// <para>
    /// Discards share <see cref="DiscardedFrameCount"/> with the reference-based path and the
    /// same <see cref="MaxDiscardedFrames"/> cap. The two never run in one session — a session
    /// either inherits a counter value or it does not — so the counter means the same thing
    /// either way: frames this session threw away.
    /// </para>
    /// </remarks>
    /// <param name="message">The frame that just arrived.</param>
    /// <param name="heldFrame">
    /// The frame that was held, for the two outcomes that have something to say about it: the
    /// frame to report dropped for <see cref="StreamFrameAdmission.DiscardHeldFrame"/>, and the
    /// frame to deliver first for
    /// <see cref="StreamFrameAdmission.ReleaseHeldFrameThenThisOne"/>. Null for
    /// <see cref="StreamFrameAdmission.Hold"/>.
    /// </param>
    /// <returns>What the caller must do with the frames.</returns>
    public StreamFrameAdmission AdmitFirstFrameOfSession(
        DaqifiOutMessage message,
        out DaqifiOutMessage? heldFrame)
    {
        var held = _heldFirstFrame;
        if (held is null)
        {
            _heldFirstFrame = message;
            TrackFrame(message.MsgTimeStamp);
            heldFrame = null;
            return StreamFrameAdmission.Hold;
        }

        heldFrame = held;
        TrackFrame(message.MsgTimeStamp);

        if (_discardedFrameCount < MaxDiscardedFrames
            && TicksSince(held.MsgTimeStamp, message.MsgTimeStamp) >= _leftoverWindowTicks)
        {
            _discardedFrameCount++;
            _heldFirstFrame = message;
            return StreamFrameAdmission.DiscardHeldFrame;
        }

        _heldFirstFrame = null;
        _awaitingFirstFramePair = false;
        return StreamFrameAdmission.ReleaseHeldFrameThenThisOne;
    }

    /// <summary>
    /// Device ticks from <paramref name="from"/> to <paramref name="to"/>, using modular
    /// <see cref="uint"/> subtraction so the result stays correct when the counter wraps between
    /// the two.
    /// </summary>
    private static double TicksSince(uint from, uint to) => unchecked(to - from);
}
