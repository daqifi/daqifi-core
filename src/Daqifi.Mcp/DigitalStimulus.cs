using System.Diagnostics;
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
/// moved, not when it did. A caller that needs the edge on the device's clock enables the driven
/// channel and reads it off that column.
/// </para>
/// <para>
/// Nothing here is thread-safe by locking, and nothing needs to be: the schedule is the only
/// writer while it runs, and <see cref="FinishAsync"/> waits for it to stop before touching the
/// pin itself.
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

    private readonly CancellationTokenSource _stop = new();
    private Task _schedule = Task.CompletedTask;

    private long _origin;
    private long? _edgeIssuedAt;
    private long? _releasedAt;

    /// <param name="drive">Drives the pin high (<c>true</c>) or low. May throw; see <see cref="FinishAsync"/>.</param>
    /// <param name="level">The level the edge drives the pin to. The pin is released to the opposite one.</param>
    /// <param name="delay">How long after the first sample to issue the edge.</param>
    /// <param name="pulseWidth">How long after the edge to release the pin, or null to leave it (a step).</param>
    internal DigitalStimulus(Action<bool> drive, bool level, TimeSpan delay, TimeSpan? pulseWidth)
    {
        _drive = drive;
        _level = level;
        _delay = delay;
        _pulseWidth = pulseWidth;
    }

    /// <summary>
    /// When the edge was issued, in milliseconds after the first sample arrived; null when the
    /// capture ended before it was due.
    /// </summary>
    internal int? EdgeIssuedAtMs => SinceFirstSample(_edgeIssuedAt);

    /// <summary>
    /// When the pin was returned to its baseline level, on the same clock as
    /// <see cref="EdgeIssuedAtMs"/>; null when it never was (a step, or an edge that never fired).
    /// </summary>
    internal int? ReleasedAtMs => SinceFirstSample(_releasedAt);

    /// <summary>Whether the pin is sitting at the edge's level rather than its baseline.</summary>
    internal bool IsAsserted => _edgeIssuedAt is not null && _releasedAt is null;

    /// <summary>
    /// Starts waiting for the first sample. Call it on the flow the writes must run on — see the
    /// caller for why that matters.
    /// </summary>
    internal void Start() => _schedule = RunAsync(_stop.Token);

    /// <summary>Marks the capture's first sample, which is what the delay is counted from.</summary>
    internal void OnFirstSample() => _firstSampleAt.TrySetResult(Stopwatch.GetTimestamp());

    /// <summary>
    /// Stops the schedule and leaves the pin where the caller is owed it: a pulse is always
    /// released, and a step is undone when <paramref name="restoreStep"/> says the capture did
    /// not complete — a failed or cancelled call should not leave an output asserted that nobody
    /// was told about.
    /// </summary>
    /// <remarks>
    /// Throws whatever the pin write throws, including the one the schedule hit if the edge
    /// itself failed. The release is still attempted in that case: a write that threw may have
    /// reached the device anyway.
    /// </remarks>
    internal async Task FinishAsync(bool restoreStep)
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await _schedule.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped part-way through a wait, which is how every capture that outlives its
                // stimulus's schedule ends it.
            }
        }
        finally
        {
            _stop.Dispose();

            if (IsAsserted && (_pulseWidth is not null || restoreStep))
            {
                Release();
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _origin = await _firstSampleAt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Whatever is left of the delay once the wake-up for the first sample has been paid for.
        var remaining = _delay - Stopwatch.GetElapsedTime(_origin);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        // Stamped before the write, not after: the pin cannot have moved earlier than this, which
        // is a claim that stays true however long the transport takes.
        _edgeIssuedAt = Stopwatch.GetTimestamp();
        _drive(_level);

        if (_pulseWidth is { } width)
        {
            await Task.Delay(width, cancellationToken).ConfigureAwait(false);
            Release();
        }
    }

    private void Release()
    {
        _releasedAt = Stopwatch.GetTimestamp();
        _drive(!_level);
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
