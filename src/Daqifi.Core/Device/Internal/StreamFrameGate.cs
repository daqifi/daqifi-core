using Daqifi.Core.Communication.Messages;

#nullable enable

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
/// <b>Known limitation.</b> This needs a counter value from before the session, which means the
/// very first session after a connect is unprotected: the device's latched frame survives a
/// disconnect, but a freshly connected instance has nothing to recognize it against. Every
/// session from the second onward — which is the stop/start case #533 describes — is covered.
/// The alternative, holding the first frame of the first session until a second frame can vouch
/// for it, was considered and rejected: it delays every consumer's first sample to defend
/// against a frame that has never been observed on current firmware.
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
    /// Gets a value indicating whether the gate still has something to decide. False for the
    /// overwhelming majority of frames, which lets the caller skip evaluation entirely.
    /// </summary>
    public bool IsValidating => _checkForLeftoverFrames;

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
    public void BeginSession(uint timestampFrequency, int streamingFrequencyHz)
    {
        var ticksPerSecond = timestampFrequency != 0
            ? timestampFrequency
            : TimestampProcessor.DefaultTimestampFrequency;
        var rate = streamingFrequencyHz > 0 ? streamingFrequencyHz : FallbackStreamingFrequencyHz;
        _leftoverWindowTicks = LeftoverWindowSamplePeriods * (ticksPerSecond / (double)rate);

        _discardedFrameCount = 0;
        _sessionStartReference = _lastSeenDeviceTimestamp;
        _checkForLeftoverFrames = _hasDeviceTimestampReference;
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
    /// Device ticks from <paramref name="from"/> to <paramref name="to"/>, using modular
    /// <see cref="uint"/> subtraction so the result stays correct when the counter wraps between
    /// the two.
    /// </summary>
    private static double TicksSince(uint from, uint to) => unchecked(to - from);
}
