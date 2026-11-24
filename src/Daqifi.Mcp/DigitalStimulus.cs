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
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and travel to the pin over the same
/// transport as everything else, so the edge lands a few milliseconds after it was asked for
/// (more on a busy host, or on Windows, whose default timer tick is ~15 ms). What is reported is
/// when each write was <b>issued</b>, in whole milliseconds — the earliest the pin can have
/// moved, not when it did, and not proof that it did: Core queues a command for its writer
/// thread and returns, so a write that later fails on the wire is not reported back here. A
/// caller that needs the edge itself enables the driven channel and reads it off that column.
/// </para>
/// <para>
/// The schedule is the only writer while it runs, and <see cref="FinishAsync"/> waits for it to
/// stop before touching the pin itself. The one thing the two race for is whether the edge goes
/// out at all, and that is settled under <see cref="_decision"/>: once the capture has ended, an
/// edge that had not yet been issued never is. A timer that had already fired, or a first-sample
/// wake-up still queued, would otherwise drive the pin after the rows it was meant to appear in
/// had been returned.
/// </para>
/// </remarks>
internal sealed class DigitalStimulus
{
    private readonly Action<bool> _drive;
    private readonly bool _level;
    private readonly TimeSpan _delay;
    private readonly TimeSpan? _pulseWidth;

    private readonly TaskCompletionSource<long> _firstSampleAt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _decision = new();
    private bool _captureEnded;
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
    internal void Start() => _schedule = RunAsync(_stop.Token);

    /// <summary>Marks the capture's first sample, which is what the delay is counted from.</summary>
    internal void OnFirstSample() => _firstSampleAt.TrySetResult(Stopwatch.GetTimestamp());

    /// <summary>
    /// Ends the schedule and leaves the pin where the caller is owed it: a pulse is always
    /// released, and a step is undone when <paramref name="restoreStep"/> says the capture did
    /// not complete, or when the schedule itself failed — a call that is about to fail should not
    /// leave an output asserted that nobody was told about.
    /// </summary>
    /// <remarks>
    /// An edge that has not been issued by the time this is called never is. A write the
    /// schedule failed on is surfaced from here, after the release has been attempted: a write
    /// that threw may have reached the device anyway. If that release fails too, the schedule's
    /// failure is still the one reported, since it is the one that explains the other.
    /// </remarks>
    internal async Task FinishAsync(bool restoreStep)
    {
        lock (_decision)
        {
            _captureEnded = true;
        }

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

        if (IsAsserted && (_pulseWidth is not null || restoreStep))
        {
            Release();
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

        // Whatever is left of the delay once the wake-up for the first sample has been paid for.
        var remaining = _delay - Stopwatch.GetElapsedTime(_origin);
        if (remaining > TimeSpan.Zero)
        {
            await _wait(remaining, cancellationToken).ConfigureAwait(false);
        }

        // Getting here does not mean the capture is still running: a wait that completed just
        // ahead of the cancel resumes normally. Whether the edge goes out is decided against the
        // capture's end under one lock, so it is either issued before the end or not at all.
        lock (_decision)
        {
            if (_captureEnded)
            {
                return;
            }

            // Stamped before the write, not after: the pin cannot have moved earlier than this,
            // which is a claim that stays true however long the transport takes. It also means a
            // write that throws still counts as asserted, so the release is attempted.
            _edgeIssuedAt = Stopwatch.GetTimestamp();
        }

        _drive(_level);

        if (_pulseWidth is { } width)
        {
            await _wait(width, cancellationToken).ConfigureAwait(false);
            Release();
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
