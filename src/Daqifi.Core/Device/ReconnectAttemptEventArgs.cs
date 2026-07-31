namespace Daqifi.Core.Device;

/// <summary>
/// Reports that an automatic reconnect attempt is about to be made (issue #379).
/// </summary>
/// <remarks>
/// Raised once per attempt, before the backoff wait, so a UI can show both which attempt is
/// running and how long it will be until anything happens.
/// </remarks>
public class ReconnectAttemptEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReconnectAttemptEventArgs"/> class.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt number.</param>
    /// <param name="maxAttempts">The total number of attempts the policy allows.</param>
    /// <param name="delay">How long the device will wait before making this attempt.</param>
    /// <param name="previousError">Why the previous attempt failed, or <c>null</c> for the first.</param>
    public ReconnectAttemptEventArgs(
        int attemptNumber,
        int maxAttempts,
        TimeSpan delay,
        Exception? previousError = null)
    {
        AttemptNumber = attemptNumber;
        MaxAttempts = maxAttempts;
        Delay = delay;
        PreviousError = previousError;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>Gets the 1-based number of the attempt about to be made.</summary>
    public int AttemptNumber { get; }

    /// <summary>Gets how many attempts the policy allows in total.</summary>
    public int MaxAttempts { get; }

    /// <summary>Gets the backoff wait that precedes this attempt.</summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// Gets the failure that ended the previous attempt, or <c>null</c> when this is the first
    /// attempt after the drop.
    /// </summary>
    public Exception? PreviousError { get; }

    /// <summary>Gets the UTC time at which the attempt was scheduled.</summary>
    public DateTime Timestamp { get; }
}
