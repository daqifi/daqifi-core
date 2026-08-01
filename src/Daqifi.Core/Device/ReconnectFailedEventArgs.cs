namespace Daqifi.Core.Device;

/// <summary>
/// Reports that automatic reconnection has stopped without restoring the session (issue #379) —
/// either because every allowed attempt failed, or because it was cancelled.
/// </summary>
/// <remarks>
/// Giving up is the loud outcome: alongside this event the failure is logged, raised on
/// <see cref="DaqifiDevice.ErrorOccurred"/> with
/// <see cref="DeviceErrorSource.Reconnect"/>, and the device settles on
/// <see cref="ConnectionStatus.Failed"/>. Cancellation is quieter — it is what the caller asked
/// for — and leaves the device on <see cref="ConnectionStatus.Lost"/>.
/// </remarks>
public class ReconnectFailedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReconnectFailedEventArgs"/> class.
    /// </summary>
    /// <param name="attemptsMade">How many attempts were made.</param>
    /// <param name="lastError">The failure that ended the final attempt, if there was one.</param>
    /// <param name="wasCanceled">Whether reconnection stopped because it was cancelled.</param>
    public ReconnectFailedEventArgs(int attemptsMade, Exception? lastError, bool wasCanceled)
    {
        AttemptsMade = attemptsMade;
        LastError = lastError;
        WasCanceled = wasCanceled;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>Gets how many reconnect attempts were made before giving up.</summary>
    public int AttemptsMade { get; }

    /// <summary>
    /// Gets the failure that ended the last attempt, or <c>null</c> when reconnection was
    /// cancelled before any attempt failed.
    /// </summary>
    public Exception? LastError { get; }

    /// <summary>
    /// Gets a value indicating whether reconnection stopped because it was cancelled — by
    /// <see cref="DaqifiDevice.CancelReconnect"/>, <see cref="DaqifiDevice.Disconnect"/>, or
    /// disposal — rather than by exhausting <see cref="ReconnectOptions.MaxAttempts"/>.
    /// </summary>
    public bool WasCanceled { get; }

    /// <summary>Gets the UTC time at which reconnection stopped.</summary>
    public DateTime Timestamp { get; }
}
