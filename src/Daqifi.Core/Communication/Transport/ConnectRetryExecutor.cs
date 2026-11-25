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
    /// <param name="teardownToken">
    /// Signalled when the transport is disconnected or disposed. It ends the whole operation, not
    /// just the attempt in flight: a retry after it would reopen a connection the caller has
    /// explicitly closed. Nothing more is reported through <paramref name="onStatusChanged"/> once
    /// it is signalled, because the teardown reports itself and a later connect may already have.
    /// </param>
    /// <param name="createTornDownException">
    /// Builds what a connect ended by <paramref name="teardownToken"/> throws, from whatever the
    /// attempt failed with.
    /// </param>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public static async Task ExecuteAsync(
        ConnectionRetryOptions? retryOptions,
        Func<ConnectionRetryOptions, CancellationToken, Task> connectAttempt,
        Action onAttemptFailed,
        Action<bool, Exception?> onStatusChanged,
        CancellationToken cancellationToken = default,
        CancellationToken teardownToken = default,
        Func<Exception, Exception>? createTornDownException = null)
    {
        var options = retryOptions ?? ConnectionRetryOptions.NoRetry;
        var maxAttempts = options.Enabled ? options.MaximumAttempts : 1;

        cancellationToken.ThrowIfCancellationRequested();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, teardownToken);
        var stopToken = stop.Token;

        // No loop condition on purpose: an iteration returns on success, throws on cancellation
        // or on the final failure, and continues only while attempt < maxAttempts, so the body
        // runs at most maxAttempts times and control never falls out of the loop. maxAttempts is
        // always at least 1 (NoRetry pins it to 1 and the MaximumAttempts setter rejects anything
        // lower), so the first attempt always runs.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Checked every iteration, not just before the delay: a retry policy configured
                // with no backoff skips the delay entirely, and without this a cancellation that
                // arrived during the previous attempt would be answered with another dial.
                stopToken.ThrowIfCancellationRequested();

                // Calculate delay for this attempt
                if (attempt > 1)
                {
                    var delay = options.CalculateDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, stopToken).ConfigureAwait(false);
                    }
                }

                await connectAttempt(options, stopToken).ConfigureAwait(false);

                // A cancel that lands as the attempt completes is still a cancel: announcing a
                // connection the caller has given up on would leave it open with no owner.
                stopToken.ThrowIfCancellationRequested();

                onStatusChanged(true, null);
                return; // Success!
            }
            catch (Exception ex) when (teardownToken.IsCancellationRequested)
            {
                // Disconnected or disposed underneath this connect, whatever the attempt made of it.
                onAttemptFailed();
                cancellationToken.ThrowIfCancellationRequested();
                throw createTornDownException?.Invoke(ex) ?? ex;
            }
            catch (OperationCanceledException ex) when (stopToken.IsCancellationRequested)
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
    }
}
