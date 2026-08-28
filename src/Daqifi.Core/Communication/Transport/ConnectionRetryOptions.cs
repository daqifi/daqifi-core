namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Configuration options for connection retry behavior with exponential backoff.
/// </summary>
/// <remarks>
/// Every value is validated where it is set, so a misconfiguration is reported against the
/// property the caller actually wrote — and reported before a connect is attempted at all
/// (issue #681). Left to the platform, a zero or negative <see cref="ConnectionTimeout"/>
/// surfaced as four different exceptions naming properties the caller never touched
/// (<c>WriteTimeout</c>, <c>ReadTimeout</c>, a socket error), and, because the retry executor
/// cannot tell a permanent misconfiguration from a transient connect failure, it then sat
/// through the whole backoff curve re-dialling a healthy device.
/// </remarks>
public class ConnectionRetryOptions
{
    private int _maxAttempts = 3;
    private TimeSpan _initialDelay = TimeSpan.FromSeconds(1);
    private TimeSpan _maxDelay = TimeSpan.FromSeconds(30);
    private double _backoffMultiplier = 2.0;
    private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of retry attempts. Default is 3.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxAttempts), value, "At least one connection attempt must be allowed.");
            }

            _maxAttempts = value;
        }
    }

    /// <summary>
    /// Gets or sets the initial delay before the first retry attempt. Default is 1 second.
    /// Zero means retry immediately.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public TimeSpan InitialDelay
    {
        get => _initialDelay;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(InitialDelay), value, "The delay cannot be negative.");
            }

            _initialDelay = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts. Default is 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public TimeSpan MaxDelay
    {
        get => _maxDelay;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxDelay), value, "The delay cannot be negative.");
            }

            _maxDelay = value;
        }
    }

    /// <summary>
    /// Gets or sets the backoff multiplier for exponential backoff. Default is 2.0; 1.0 gives a
    /// fixed delay.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    public double BackoffMultiplier
    {
        get => _backoffMultiplier;
        set
        {
            // Negated rather than `value < 1.0` so NaN — which compares false against everything —
            // is rejected too, instead of turning every backoff into NaN milliseconds.
            if (!(value >= 1.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(BackoffMultiplier), value, "The backoff multiplier must be at least 1.0.");
            }

            _backoffMultiplier = value;
        }
    }

    /// <summary>
    /// Gets or sets the connection timeout for each attempt. Default is 5 seconds.
    /// </summary>
    /// <remarks>
    /// Both bounds exist because both transports hand this value to the platform as a millisecond
    /// <see cref="int"/>. A sub-millisecond span truncates to zero, which the platform rejects
    /// exactly as it rejected an outright zero; anything longer than <see cref="int.MaxValue"/>
    /// milliseconds wraps round to a negative timeout, which it also rejects. Both are the very
    /// error this validation exists to keep away from the retry loop.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is under 1 millisecond (zero and negatives included) or longer than
    /// <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </exception>
    public TimeSpan ConnectionTimeout
    {
        get => _connectionTimeout;
        set
        {
            if (value.TotalMilliseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ConnectionTimeout), value,
                    "The connection timeout must be at least 1 millisecond.");
            }

            if (value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ConnectionTimeout), value,
                    $"The connection timeout cannot exceed {int.MaxValue} milliseconds (about 24.8 days).");
            }

            _connectionTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets whether retry is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Creates a default configuration with retry disabled.
    /// </summary>
    public static ConnectionRetryOptions NoRetry => new() { Enabled = false, MaxAttempts = 1 };

    /// <summary>
    /// Creates a configuration optimized for fast reconnection (short delays, fewer attempts).
    /// </summary>
    public static ConnectionRetryOptions Fast => new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(500),
        MaxDelay = TimeSpan.FromSeconds(5),
        BackoffMultiplier = 1.5,
        ConnectionTimeout = TimeSpan.FromSeconds(3)
    };

    /// <summary>
    /// Creates a configuration optimized for slow/unreliable connections (longer delays, more attempts).
    /// </summary>
    public static ConnectionRetryOptions Resilient => new()
    {
        MaxAttempts = 5,
        InitialDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(60),
        BackoffMultiplier = 2.5,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Calculates the delay for a specific retry attempt using exponential backoff.
    /// </summary>
    /// <param name="attemptNumber">The attempt number (1-based).</param>
    /// <returns>The calculated delay for this attempt.</returns>
    public TimeSpan CalculateDelay(int attemptNumber)
    {
        if (attemptNumber <= 1)
            return TimeSpan.Zero;

        // Zero in, zero out. A caller who asked for immediate retries must get them at every
        // attempt number: multiplying zero by the growing backoff factor is still zero, right up
        // until that factor overflows Math.Pow to infinity, where 0 × ∞ is NaN and the arithmetic
        // below would hand back MaxDelay — the exact opposite of the configured policy.
        if (InitialDelay == TimeSpan.Zero)
            return TimeSpan.Zero;

        // Math.Pow overflows to +Infinity for a large enough attempt count. With a positive
        // InitialDelay the product is then +Infinity too, and Math.Min yields MaxDelay — the
        // intended cap. NaN is unreachable now that a zero InitialDelay returns above and
        // BackoffMultiplier rejects NaN, so there is nothing further to guard here.
        var delay = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attemptNumber - 2);
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxDelay.TotalMilliseconds));
    }
}
