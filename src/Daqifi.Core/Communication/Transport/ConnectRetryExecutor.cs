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
    /// connection timeout, and the caller's cancellation token so it can abandon an
    /// attempt already in flight. Throwing signals a failed attempt.
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
    /// <param name="cancellationToken">
    /// Observed between attempts, while waiting out a backoff delay, and by
    /// <paramref name="connectAttempt"/> itself. A cancellation is never treated as a retryable
    /// attempt failure: the loop stops immediately and the
    /// <see cref="OperationCanceledException"/> is surfaced to the caller, which is what lets an
    /// auto-reconnect loop be torn down promptly rather than after the remaining attempts.
    /// </param>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public static async Task ExecuteAsync(
        ConnectionRetryOptions? retryOptions,
        Func<ConnectionRetryOptions, CancellationToken, Task> connectAttempt,
        Action onAttemptFailed,
        Action<bool, Exception?> onStatusChanged,
        CancellationToken cancellationToken = default)
    {
        var options = retryOptions ?? ConnectionRetryOptions.NoRetry;
        var maxAttempts = options.Enabled ? options.MaxAttempts : 1;
        Exception? lastException = null;

        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Checked every iteration, not just before the delay: a retry policy configured
                // with no backoff skips the delay entirely, and without this a cancellation that
                // arrived during the previous attempt would be answered with another dial.
                cancellationToken.ThrowIfCancellationRequested();

                // Calculate delay for this attempt
                if (attempt > 1)
                {
                    var delay = options.CalculateDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                }

                await connectAttempt(options, cancellationToken);
                onStatusChanged(true, null);
                return; // Success!
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up on this connect. Clean up the half-built handle exactly as a
                // failed attempt would, report the transport as disconnected, and stop — retrying
                // after a cancellation would keep the device dialling long after the caller left.
                onAttemptFailed();
                onStatusChanged(false, ex);
                throw;
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
