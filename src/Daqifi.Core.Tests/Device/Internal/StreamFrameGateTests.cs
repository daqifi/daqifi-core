using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="StreamFrameGate"/>, the cross-session leftover-frame guard
/// (daqifi-nyquist-firmware #533). A tick frequency of 1000 Hz is used throughout so one tick is
/// one millisecond and the window arithmetic can be read off the timestamps directly.
/// </summary>
public class StreamFrameGateTests
{
    private const uint TicksPerSecond = 1000;

    [Fact]
    public void IsValidating_FalseWithoutACounterReference()
    {
        // Nothing was ever seen, so there is nothing to compare a first frame against: the gate
        // stands aside rather than guessing.
        var gate = new StreamFrameGate();
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        Assert.False(gate.IsValidating);
    }

    [Fact]
    public void IsValidating_TrueOnceAFrameHasBeenSeen()
    {
        var gate = new StreamFrameGate();
        gate.TrackFrame(1000);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        Assert.True(gate.IsValidating);
    }

    [Fact]
    public void LeftoverInsideTheWindow_IsRejected_AndAGenuineFrameOpensTheGate()
    {
        // 1 Hz -> 1000-tick sample period -> 2500-tick window.
        var gate = new StreamFrameGate();
        gate.TrackFrame(10_000);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        // Latched frame: one sample period past the previous session's last frame.
        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(11_000)));
        Assert.Equal(1, gate.DiscardedFrameCount);
        Assert.True(gate.IsValidating);

        // Genuine first frame: a full stop-to-start gap on.
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(40_000)));
        Assert.False(gate.IsValidating); // gate steps aside for the rest of the session
    }

    [Fact]
    public void CounterWrap_GenuineFrameAfterTheWrapIsNotMistakenForALeftover()
    {
        // The counter is a uint that wraps. Measured with modular subtraction this frame is 11_001
        // ticks past the reference and therefore genuine; measured naively it looks like a large
        // negative delta, which would put it inside the window and drop real data.
        var gate = new StreamFrameGate();
        gate.TrackFrame(uint.MaxValue - 1000);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(10_000)));
        Assert.Equal(0, gate.DiscardedFrameCount);
    }

    [Fact]
    public void CounterWrap_LeftoverStraddlingTheWrapIsStillRejected()
    {
        // Reference sits 1001 ticks below the wrap; the latched frame lands 500 ticks past it,
        // 1501 ticks on in modular terms — inside the 2500-tick window.
        var gate = new StreamFrameGate();
        gate.TrackFrame(uint.MaxValue - 1000);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(500)));
        Assert.Equal(1, gate.DiscardedFrameCount);
    }

    [Fact]
    public void WindowScalesWithTheSampleRate()
    {
        // The same 300-tick delta is well inside one sample period at 1 Hz and several periods on
        // at 10 Hz. A fixed seconds-based window would drop the second case.
        var slow = new StreamFrameGate();
        slow.TrackFrame(10_000);
        slow.BeginSession(TicksPerSecond, streamingFrequencyHz: 1); // window 2500 ticks
        Assert.True(slow.IsLeftoverFromPreviousSession(Frame(10_300)));

        var fast = new StreamFrameGate();
        fast.TrackFrame(10_000);
        fast.BeginSession(TicksPerSecond, streamingFrequencyHz: 10); // window 250 ticks
        Assert.False(fast.IsLeftoverFromPreviousSession(Frame(10_300)));
    }

    [Fact]
    public void UnknownRate_FallsBackToTheWidestWindow()
    {
        // A session started without a usable rate assumes the device's slowest, which is the
        // conservative choice: the widest window.
        var gate = new StreamFrameGate();
        gate.TrackFrame(10_000);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 0);

        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(12_000)));  // inside 2500
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(13_000))); // 3000 ticks on: genuine
    }

    [Fact]
    public void UnknownTickFrequency_FallsBackToTheDefault()
    {
        // 50 MHz default tick rate at 1 Hz -> a 125_000_000-tick window.
        var gate = new StreamFrameGate();
        gate.TrackFrame(0);
        gate.BeginSession(timestampFrequency: 0, streamingFrequencyHz: 1);

        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(100_000_000)));
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(130_000_000)));
    }

    [Fact]
    public void QuickRestart_DiscardsABoundedPrefixInsteadOfCascading()
    {
        // Worst case for the heuristic: a restart so fast that genuine frames land inside the
        // window. Because every frame is measured against the counter fixed at session start, the
        // deltas grow by one period each time and the run ends on its own — two discards here, not
        // an unbounded cascade. (Measuring against the *previous* frame instead would discard every
        // frame in the session, each sitting one period from its discarded predecessor.)
        var gate = new StreamFrameGate();
        gate.TrackFrame(0);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1); // window 2500 ticks

        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(1000)));
        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(2000)));
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(3000)));
        Assert.Equal(2, gate.DiscardedFrameCount);
    }

    [Fact]
    public void DiscardsAreCappedSoAStreamCanNeverBeWithheldIndefinitely()
    {
        // A device whose counter barely advances would otherwise be discarded forever. After the
        // cap the gate gives up and lets everything through.
        var gate = new StreamFrameGate();
        gate.TrackFrame(0);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        for (uint ts = 1; ts <= StreamFrameGate.MaxDiscardedFrames; ts++)
        {
            Assert.True(gate.IsLeftoverFromPreviousSession(Frame(ts)));
        }

        Assert.Equal(StreamFrameGate.MaxDiscardedFrames, gate.DiscardedFrameCount);
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(StreamFrameGate.MaxDiscardedFrames + 1)));
        Assert.False(gate.IsValidating);
    }

    [Fact]
    public void BeginSession_ResetsTheDiscardCountAndRe_AnchorsTheReference()
    {
        var gate = new StreamFrameGate();
        gate.TrackFrame(0);
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);
        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(1000)));
        Assert.Equal(1, gate.DiscardedFrameCount);

        // The next session is anchored to the last frame seen (1000), not to the old reference.
        gate.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);
        Assert.Equal(0, gate.DiscardedFrameCount);
        Assert.True(gate.IsLeftoverFromPreviousSession(Frame(2000)));  // 1000 ticks past 1000
        Assert.False(gate.IsLeftoverFromPreviousSession(Frame(60_000)));
    }

    private static DaqifiOutMessage Frame(uint timestamp) => new() { MsgTimeStamp = timestamp };
}
