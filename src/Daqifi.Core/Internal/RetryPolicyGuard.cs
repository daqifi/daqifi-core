namespace Daqifi.Core.Internal;

/// <summary>
/// The range checks shared by the library's two exponential-backoff policy classes:
/// <see cref="Communication.Transport.ConnectionRetryOptions"/>, which governs the initial
/// connect, and <see cref="Device.ReconnectOptions"/>, which governs re-establishing a session
/// that was lost.
/// </summary>
/// <remarks>
/// The two policies are deliberately separate settings for separate moments, and their backoff
/// arithmetic genuinely differs — only the value guards were identical, and they were identical
/// line for line. Keeping one copy means the reasoning behind each guard, in particular the
/// NaN-rejecting form of the backoff check, cannot survive in one policy and quietly go missing
/// from the other.
/// </remarks>
internal static class RetryPolicyGuard
{
    /// <summary>
    /// Rejects an attempt count below one — a policy that would never try at all.
    /// </summary>
    /// <param name="value">The proposed attempt count.</param>
    /// <param name="paramName">The name of the property being written.</param>
    /// <param name="message">
    /// The failure message, which names the kind of attempt the calling policy makes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    internal static void RequireAtLeastOneAttempt(int value, string paramName, string message)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, value, message);
        }
    }

    /// <summary>
    /// Rejects a negative delay.
    /// </summary>
    /// <param name="value">The proposed delay.</param>
    /// <param name="paramName">The name of the property being written.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    internal static void RequireNonNegativeDelay(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The delay cannot be negative.");
        }
    }

    /// <summary>
    /// Rejects a backoff multiplier that would shrink the delay, or that is not a number.
    /// </summary>
    /// <param name="value">The proposed multiplier.</param>
    /// <param name="paramName">The name of the property being written.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    internal static void RequireGrowingBackoffMultiplier(double value, string paramName)
    {
        // Negated rather than `value < 1.0` so NaN — which compares false against everything —
        // is rejected too, instead of turning every backoff into NaN milliseconds.
        if (!(value >= 1.0))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, "The backoff multiplier must be at least 1.0.");
        }
    }
}
