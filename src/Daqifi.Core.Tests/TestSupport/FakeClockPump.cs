using Microsoft.Extensions.Time.Testing;
using System.Diagnostics;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Drives a <see cref="FakeTimeProvider"/> forward on behalf of code that is waiting on it from
/// another thread (issue #637).
/// </summary>
/// <remarks>
/// <para>
/// A test that owns the clock outright can simply call <c>Advance</c> and be done. These helpers
/// exist for the other case: the code under test runs its own loop — a reconnect supervisor, an SD
/// transfer, a text exchange — and registers each wait only when it reaches it. There is no moment
/// a test can identify from outside at which "the timer now exists", so advancing once and waiting
/// is a race the test loses whenever the loop is a little slower than expected.
/// </para>
/// <para>
/// Stepping repeatedly removes the race, and it is safe in the only direction that matters:
/// advancing before a wait is registered does not fire anything, it just moves the clock, and the
/// wait is then created relative to the new now. So overshooting can only ever grant the code under
/// test <em>more</em> device time than the test intended, never less. Assertions built on these
/// helpers therefore have to be one-sided — "it did not give up before its budget", not "it gave up
/// at exactly its budget" — unless the slice is small enough that the difference does not matter.
/// </para>
/// <para>
/// The slice size is the caller's, because it is both the measurement's resolution and the amount
/// of overshoot the caller is willing to tolerate. A caller stepping towards a near deadline it
/// must not overshoot (an SD transfer that has to reach the wire before its watchdog fires) uses a
/// small slice; one that just needs to blow through a far deadline uses a large one.
/// </para>
/// </remarks>
public static class FakeClockPump
{
    /// <summary>
    /// Default real-time bound. Nothing driven by these helpers is supposed to take real time, so
    /// this only ever fires on a test that has already failed — it turns a hang into a failure.
    /// </summary>
    public static readonly TimeSpan DefaultRealTimeBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Steps <paramref name="clock"/> in <paramref name="slice"/>-sized jumps until
    /// <paramref name="until"/> completes, and reports how much device time that took.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws when the real-time bound is hit: the caller knows what the
    /// failure means for its own scenario and can say so, which reads better than a bare timeout
    /// from in here. Every caller asserts on <paramref name="until"/> afterwards.
    /// </remarks>
    /// <param name="clock">The clock to advance.</param>
    /// <param name="until">The work to wait for.</param>
    /// <param name="slice">How far to jump per step.</param>
    /// <param name="realTimeBound">
    /// Real time after which to stop stepping, whatever <paramref name="until"/> is doing.
    /// Defaults to <see cref="DefaultRealTimeBound"/>.
    /// </param>
    /// <returns>The total device time advanced.</returns>
    public static async Task<TimeSpan> UntilAsync(
        FakeTimeProvider clock,
        Task until,
        TimeSpan slice,
        TimeSpan? realTimeBound = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(until);

        var advanced = TimeSpan.Zero;
        var bound = Stopwatch.StartNew();
        var limit = realTimeBound ?? DefaultRealTimeBound;

        while (!until.IsCompleted && bound.Elapsed < limit)
        {
            clock.Advance(slice);
            advanced += slice;

            // The one genuinely real wait in here, and it is a yield rather than a pace: it hands
            // the thread pool a chance to run the continuations the Advance above just released.
            await Task.Delay(1).ConfigureAwait(false);
        }

        return advanced;
    }

    /// <summary>
    /// Steps <paramref name="clock"/> a fixed distance in <paramref name="slice"/>-sized jumps,
    /// letting continuations run between them.
    /// </summary>
    /// <remarks>
    /// For the "and nothing happened yet" half of a test: advance a little, then assert the work is
    /// still outstanding, before advancing the rest of the way.
    /// </remarks>
    /// <param name="clock">The clock to advance.</param>
    /// <param name="total">How much device time to advance in all.</param>
    /// <param name="slice">How far to jump per step.</param>
    public static async Task ForAsync(FakeTimeProvider clock, TimeSpan total, TimeSpan slice)
    {
        ArgumentNullException.ThrowIfNull(clock);

        for (var advanced = TimeSpan.Zero; advanced < total; advanced += slice)
        {
            clock.Advance(slice);
            await Task.Delay(1).ConfigureAwait(false);
        }
    }
}
