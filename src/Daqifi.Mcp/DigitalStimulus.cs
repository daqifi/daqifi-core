using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Daqifi.Core.Channel;

namespace Daqifi.Mcp;

/// <summary>
/// Drives a digital pin at a known moment inside a running capture — the half of
/// <c>drive_and_capture</c> that is not the capture. The pin is driven to its level
/// <c>delay</c> after the capture's first sample and, for a pulse, back again
/// <c>pulseWidth</c> later.
/// </summary>
/// <remarks>
/// <para>
/// The clock starts at the first sample rather than at the call, so the delay is how much
/// baseline the rows hold before the edge. A capture that has to start the device's stream
/// spends its first ~100 ms with nothing arriving; timed from the call, a short delay would put
/// the edge ahead of the first row and the "before" level would not be in the data at all. A
/// stream that never produces a sample therefore never fires the edge either, which is the right
/// outcome: there would be nothing to see it in.
/// </para>
/// <para>
/// Timing is this machine's, not the device's. The writes are scheduled with
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and then queued by Core for its writer
/// thread, carried by the transport and acted on by the firmware, so the pin moves some time
/// after the write was asked for, by an amount nothing here measures. What is reported is when
/// each write was <b>issued</b>, in whole milliseconds — the earliest the pin can have moved, not
/// when it did, and not proof that it did: a write that later fails on the wire is not reported
/// back here. A caller that needs the edge itself enables the driven channel and reads it off
/// that column.
/// </para>
/// <para>
/// It runs on a timer of its own, not off the samples, on purpose: a pulse has to be released on
/// time even when the stream it is being watched through stalls. That makes it concurrent with
/// the capture, and three rules keep the two from leaving the pin somewhere nobody chose:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No edge after sampling has ended.</b> <see cref="Disarm"/> and the decision to issue the
/// edge share <see cref="_decision"/>, so the edge is either issued while rows were still being
/// taken or never. A timer that had already fired, or a wake-up still queued, cannot drive the pin
/// while the stream is being torn down.
/// </description></item>
/// <item><description>
/// <b>A write that fails is answered at once.</b> The baseline is attempted there and then, and
/// the capture is told to stop, rather than leaving the pin as it is until the capture's window
/// runs out.
/// </description></item>
/// <item><description>
/// <b>The way out always tries the baseline once more</b> if the pin still reads as asserted —
/// see <see cref="FinishAsync"/>.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class DigitalStimulus
{
    private readonly Action<bool> _drive;
    private readonly bool _level;
    private readonly TimeSpan _delay;
    private readonly TimeSpan? _pulseWidth;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    private readonly TaskCompletionSource<long> _firstSampleAt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _decision = new();
    private bool _disarmed;
    private Action? _onFailure;
    private Task _schedule = Task.CompletedTask;

    private long _origin;
    private long? _edgeIssuedAt;
    private long? _releasedAt;

    /// <param name="drive">Drives the pin high (<c>true</c>) or low. May throw; see <see cref="FinishAsync"/>.</param>
    /// <param name="level">The level the edge drives the pin to. The pin is released to the opposite one.</param>
    /// <param name="delay">How long after the first sample to issue the edge.</param>
    /// <param name="pulseWidth">How long after the edge to release the pin, or null to leave it (a step).</param>
    /// <param name="wait">
    /// How the schedule waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.
    /// A test seam, for putting a wait's completion and the capture's end in a chosen order.
    /// </param>
    internal DigitalStimulus(
        Action<bool> drive,
        bool level,
        TimeSpan delay,
        TimeSpan? pulseWidth,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _drive = drive;
        _level = level;
        _delay = delay;
        _pulseWidth = pulseWidth;
        _wait = wait ?? Task.Delay;
    }

    /// <summary>
    /// When the edge was issued, in milliseconds after the first sample arrived; null when the
    /// capture ended before it was due.
    /// </summary>
    internal int? EdgeIssuedAtMs => SinceFirstSample(_edgeIssuedAt);

    /// <summary>
    /// When the write returning the pin to its baseline was issued, on the same clock as
    /// <see cref="EdgeIssuedAtMs"/>; null when none was (a step, or an edge that never fired).
    /// </summary>
    internal int? ReleaseIssuedAtMs => SinceFirstSample(_releasedAt);

    /// <summary>
    /// Whether the last level this stimulus commanded is the edge's rather than the baseline.
    /// What was commanded, not what the pin is doing — see the class remarks.
    /// </summary>
    internal bool IsAsserted => _edgeIssuedAt is not null && _releasedAt is null;

    /// <summary>
    /// Starts waiting for the first sample. Call it on the flow the writes must run on — see the
    /// caller for why that matters.
    /// </summary>
    /// <param name="onFailure">
    /// Run when a scheduled write throws, after the baseline has been attempted: how the capture
    /// is told to stop reading. The failure itself is surfaced by <see cref="FinishAsync"/>.
    /// </param>
    internal void Start(Action? onFailure = null)
    {
        _onFailure = onFailure;
        _schedule = RunAsync(_stop.Token);
    }

    /// <summary>Marks the capture's first sample, which is what the delay is counted from.</summary>
    internal void OnFirstSample() => _firstSampleAt.TrySetResult(Stopwatch.GetTimestamp());

    /// <summary>
    /// Says sampling has ended: an edge not yet issued never will be. Called at the moment the
    /// capture stops taking rows, which is before the stream is torn down and well before
    /// <see cref="FinishAsync"/> — the gap in which a pending edge would otherwise still fire.
    /// A pulse already under way is unaffected; it is still released on time.
    /// </summary>
    internal void Disarm()
    {
        lock (_decision)
        {
            _disarmed = true;
        }
    }

    /// <summary>
    /// Ends the schedule and leaves the pin where the caller is owed it: a pulse is always
    /// released, and a step is left in place only when the capture completed and nothing failed —
    /// a call that is failing should not leave an output asserted that nobody was told about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write the schedule failed on is surfaced from here. So is a failure of the release this
    /// makes itself, when the capture ended mid-pulse. Either way the baseline is attempted once
    /// more first, best effort, and the first failure is the one reported.
    /// </para>
    /// <para>
    /// When the capture did not complete, nothing is thrown from here at all: whatever ended the
    /// capture is already on its way to the caller, and the baseline is best effort.
    /// </para>
    /// </remarks>
    internal async Task FinishAsync(bool captureCompleted)
    {
        Disarm();
        await _stop.CancelAsync().ConfigureAwait(false);

        ExceptionDispatchInfo? failure = null;
        try
        {
            await _schedule.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped part-way through a wait, which is how every capture that outlives its
            // stimulus's schedule ends it.
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        _stop.Dispose();

        if (failure is not null)
        {
            RestoreAfterFailure();
            failure.Throw();
        }

        if (!captureCompleted)
        {
            RestoreAfterFailure();
            return;
        }

        if (IsAsserted && _pulseWidth is not null)
        {
            try
            {
                Release();
            }
            catch
            {
                RestoreAfterFailure();
                throw;
            }
        }
    }

    /// <summary>
    /// Returns the pin to its baseline, best effort, because the call it belongs to is failing —
    /// a step included. Never throws: the failure already in flight is the one to report.
    /// </summary>
    internal void RestoreAfterFailure()
    {
        if (!IsAsserted)
        {
            return;
        }

        try
        {
            Release();
        }
        catch (Exception)
        {
            // Nothing more can be done for the pin from here, and the caller is already being
            // told the call failed.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _origin = await _firstSampleAt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Whatever is left of the delay once the wake-up for the first sample has been paid for.
            var remaining = _delay - Stopwatch.GetElapsedTime(_origin);
            if (remaining > TimeSpan.Zero)
            {
                await _wait(remaining, cancellationToken).ConfigureAwait(false);
            }

            // Getting here does not mean rows are still being taken: a wait that completed just
            // ahead of the cancel resumes normally. Whether the edge goes out is decided against
            // the end of sampling under one lock, so it is either issued before it or not at all.
            lock (_decision)
            {
                if (_disarmed)
                {
                    return;
                }

                // Stamped before the write, not after: the pin cannot have moved earlier than
                // this, which stays true however long the transport takes. It also means a write
                // that throws still counts as asserted, so the baseline is attempted.
                _edgeIssuedAt = Stopwatch.GetTimestamp();
            }

            _drive(_level);

            if (_pulseWidth is { } width)
            {
                await _wait(width, cancellationToken).ConfigureAwait(false);
                Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Now, not when the capture gets round to ending: its window can have most of a
            // minute left, and the pin is asserted. Put it back, then stop the capture so the
            // failure reaches the caller.
            RestoreAfterFailure();
            _onFailure?.Invoke();
            throw;
        }
    }

    private void Release()
    {
        // Recorded only once the write has been accepted. Stamped first, a release that threw
        // would read as done, and the clean-up that could have retried it would skip the pin.
        var issuedAt = Stopwatch.GetTimestamp();
        _drive(!_level);
        _releasedAt = issuedAt;
    }

    private int? SinceFirstSample(long? timestamp) =>
        timestamp is { } at
            ? (int)Math.Round(Stopwatch.GetElapsedTime(_origin, at).TotalMilliseconds)
            : null;
}

/// <summary>
/// Passes a capture's samples through to the sink that keeps them, and reports the first one —
/// the moment a <see cref="DigitalStimulus"/> counts its delay from.
/// </summary>
internal sealed class FirstSampleTap : ILiveSampleSink
{
    private readonly ILiveSampleSink _inner;
    private Action? _onFirstSample;

    internal FirstSampleTap(ILiveSampleSink inner, Action onFirstSample)
    {
        _inner = inner;
        _onFirstSample = onFirstSample;
    }

    public bool IsComplete => _inner.IsComplete;

    public long UnexpectedSampleCount => _inner.UnexpectedSampleCount;

    public bool Add(LiveSample sample)
    {
        _onFirstSample?.Invoke();
        _onFirstSample = null;
        return _inner.Add(sample);
    }
}
