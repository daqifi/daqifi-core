using System.Diagnostics;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Polls a condition until it holds, failing the test with a message that names what the caller
/// was waiting for.
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
