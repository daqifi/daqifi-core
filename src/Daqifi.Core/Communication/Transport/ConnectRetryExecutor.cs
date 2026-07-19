namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Shared retry/backoff scaffolding for stream transport connect operations.
/// Both <see cref="TcpStreamTransport"/> and <see cref="SerialStreamTransport"/>
/// drive their <c>ConnectAsync</c> through this executor so the attempt loop,
/// backoff delay, status reporting, and final-throw semantics stay in one place
/// (a retry/backoff fix can no longer land in one transport and miss the other).
/// Only the transport-specific "create + open handle" body and the "dispose the
/// failed handle" cleanup differ, and those are supplied by the caller.
/// </summary>
internal static class ConnectRetryExecutor
{
    /// <summary>
    /// Runs <paramref name="connectAttempt"/> under the retry policy described by
    /// <paramref name="retryOptions"/> (null selects <see cref="ConnectionRetryOptions.NoRetry"/>).
    /// </summary>
    /// <param name="retryOptions">Retry configuration, or null for a single no-retry attempt.</param>
    /// <param name="connectAttempt">
    /// Opens the transport handle. Receives the resolved options so it can honor the
    /// connection timeout. Throwing signals a failed attempt.
    /// </param>
    /// <param name="onAttemptFailed">
    /// Disposes/nulls the transport handle after a failed attempt, before the next
    /// attempt or the terminal throw.
    /// </param>
    /// <param name="onStatusChanged">
    /// Reports connection status: <c>(true, null)</c> on success, and
    /// <c>(false, error)</c> on each failure (a retry-in-progress exception between
    /// attempts, the real exception on the terminal failure).
    /// </param>
    public static async Task ExecuteAsync(
        ConnectionRetryOptions? retryOptions,
        Func<ConnectionRetryOptions, Task> connectAttempt,
        Action onAttemptFailed,
        Action<bool, Exception?> onStatusChanged)
    {
        var options = retryOptions ?? ConnectionRetryOptions.NoRetry;
        var maxAttempts = options.Enabled ? options.MaxAttempts : 1;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Calculate delay for this attempt
                if (attempt > 1)
                {
                    var delay = options.CalculateDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                }

                await connectAttempt(options);
                onStatusChanged(true, null);
                return; // Success!
            }
            catch (Exception ex)
            {
                lastException = ex;
                onAttemptFailed();

                // If this is not the last attempt and retry is enabled, continue
                if (attempt < maxAttempts && options.Enabled)
                {
                    onStatusChanged(false, new Exception($"Connection attempt {attempt}/{maxAttempts} failed, retrying...", ex));
                    continue;
                }

                // Last attempt failed or retry disabled
                onStatusChanged(false, ex);
                throw;
            }
        }

        // Should not reach here, but just in case
        onStatusChanged(false, lastException);
        throw lastException ?? new InvalidOperationException("Connection failed after all retry attempts.");
    }
}
