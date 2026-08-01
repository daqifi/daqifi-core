namespace Daqifi.Core.Device;

/// <summary>
/// Reports that a lost session has been re-established and its state restored (issue #379).
/// </summary>
/// <remarks>
/// Raised after the transport is back, the device has been re-initialized, and the channel
/// configuration — and the stream, if one was running and the policy resumes it — have been
/// re-applied. By the time this fires, samples are flowing again.
/// </remarks>
public class ReconnectedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReconnectedEventArgs"/> class.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt that succeeded.</param>
    /// <param name="outage">How long the session was down, measured from the drop.</param>
    /// <param name="streamingResumed">Whether an interrupted stream was restarted.</param>
    public ReconnectedEventArgs(int attemptNumber, TimeSpan outage, bool streamingResumed)
    {
        AttemptNumber = attemptNumber;
        Outage = outage;
        StreamingResumed = streamingResumed;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>Gets the 1-based number of the attempt that succeeded.</summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// Gets how long the session was down, from the drop being detected to the state being
    /// restored.
    /// </summary>
    public TimeSpan Outage { get; }

    /// <summary>
    /// Gets a value indicating whether a stream that was running at the time of the drop was
    /// restarted. <c>false</c> when nothing was streaming, or when
    /// <see cref="ReconnectOptions.ResumeStreaming"/> is off.
    /// </summary>
    public bool StreamingResumed { get; }

    /// <summary>Gets the UTC time at which the session was restored.</summary>
    public DateTime Timestamp { get; }
}
