using System.Diagnostics;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Polls a condition until it holds, failing the test with a message that names what the caller
/// was waiting for. <see cref="HoldsFor"/> is the negative counterpart: it watches an invariant
/// for a bounded window instead.
/// </summary>
/// <remarks>
/// The suite had grown nine copies of the same <c>DateTime.UtcNow</c> + <c>Thread.Sleep(5–10)</c>
/// loop. They disagreed about the poll interval, whether timeout returned <c>false</c> or asserted,
/// and whether the failure message described the wait. One helper keeps those choices in one place:
/// a monotonic clock, a 10 ms poll, and a required <c>because</c> so a timeout says what never
/// happened instead of "Assert.True() Failure".
/// </remarks>
public static class WaitUntil
{
    /// <summary>
    /// Default real-time bound. Generous enough for a background reader or reconnect loop to
    /// surface, short enough that a hung wait fails the test rather than the job.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gap between condition checks. Matches the 5–10 ms cadence the copies already used.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Blocks until <paramref name="condition"/> is true. Fails the test if
    /// <paramref name="timeout"/> elapses first.
    /// </summary>
    /// <param name="condition">The predicate that becomes true when the wait is satisfied.</param>
    /// <param name="because">
    /// What the caller was waiting for, in its own terms. Required: a timeout with no context
    /// is how these loops used to fail.
    /// </param>
    /// <param name="timeout">Real time after which to give up. Defaults to <see cref="DefaultTimeout"/>.</param>
    public static void That(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        That(condition, () => because, timeout);
    }

    /// <summary>
    /// Blocks until <paramref name="condition"/> is true. Fails the test if
    /// <paramref name="timeout"/> elapses first.
    /// </summary>
    /// <param name="condition">The predicate that becomes true when the wait is satisfied.</param>
    /// <param name="because">
    /// Builds the timeout message at the moment of failure, so it can include counts that
    /// changed while waiting.
    /// </param>
    /// <param name="timeout">Real time after which to give up. Defaults to <see cref="DefaultTimeout"/>.</param>
    public static void That(Func<bool> condition, Func<string> because, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(because);

        if (Try(condition, timeout ?? DefaultTimeout))
        {
            return;
        }

        Assert.True(condition(), because());
    }

    /// <summary>
    /// Asynchronously polls until <paramref name="condition"/> is true. Fails the test if
    /// <paramref name="timeout"/> elapses first.
    /// </summary>
    /// <param name="condition">The predicate that becomes true when the wait is satisfied.</param>
    /// <param name="because">
    /// What the caller was waiting for, in its own terms. Required: a timeout with no context
    /// is how these loops used to fail.
    /// </param>
    /// <param name="timeout">Real time after which to give up. Defaults to <see cref="DefaultTimeout"/>.</param>
    public static Task ThatAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        return ThatAsync(condition, () => because, timeout);
    }

    /// <summary>
    /// Asynchronously polls until <paramref name="condition"/> is true. Fails the test if
    /// <paramref name="timeout"/> elapses first.
    /// </summary>
    /// <param name="condition">The predicate that becomes true when the wait is satisfied.</param>
    /// <param name="because">
    /// Builds the timeout message at the moment of failure, so it can include counts that
    /// changed while waiting.
    /// </param>
    /// <param name="timeout">Real time after which to give up. Defaults to <see cref="DefaultTimeout"/>.</param>
    public static async Task ThatAsync(Func<bool> condition, Func<string> because, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(because);

        var limit = timeout ?? DefaultTimeout;
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < limit)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(DefaultPollInterval).ConfigureAwait(false);
        }

        Assert.True(condition(), because());
    }

    /// <summary>
    /// Checks <paramref name="invariant"/> repeatedly for the whole of <paramref name="window"/>,
    /// failing the test the moment it stops holding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For claims that something does <b>not</b> happen (no <c>Lost</c> after an intentional
    /// disconnect, no reconnect with the policy off) where the correct behaviour produces nothing
    /// a test could wait for. Waiting with <see cref="That(Func{bool}, string, TimeSpan?)"/> on a
    /// condition that is already true when the call is made returns at once and observes nothing,
    /// which is how such a test goes vacuous without anyone noticing.
    /// </para>
    /// <para>
    /// Prefer a positive signal whenever one exists: a counter moving past the point where the
    /// failure would have shown, or the background work provably finishing. Use this only where
    /// there is none, with a window sized to how long the failure would take to surface.
    /// </para>
    /// </remarks>
    /// <param name="invariant">What must stay true for the whole window.</param>
    /// <param name="because">What it means if it stops holding, in the caller's own terms.</param>
    /// <param name="window">
    /// How long to keep watching. Required: a negative observation has no sensible default length.
    /// </param>
    public static void HoldsFor(Func<bool> invariant, string because, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < window)
        {
            Assert.True(invariant(), because);
            Thread.Sleep(DefaultPollInterval);
        }

        Assert.True(invariant(), because);
    }

    private static bool Try(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(DefaultPollInterval);
        }

        return condition();
    }
}
