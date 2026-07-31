namespace Daqifi.Core.Device;

/// <summary>
/// Policy for automatically re-establishing a session after
/// <see cref="ConnectionStatus.Lost"/> (issue #379). Assign to
/// <see cref="DaqifiDevice.ReconnectOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> A freshly constructed instance has <see cref="Enabled"/> set to
/// <c>false</c>, which is exactly the behaviour a device has always had: a drop is reported as
/// <see cref="ConnectionStatus.Lost"/> and nothing else happens. Reconnect has to be asked for.
/// </para>
/// <para>
/// The shape mirrors <see cref="Communication.Transport.ConnectionRetryOptions"/>, which governs
/// the <em>initial</em> connect, so the two read the same way. They are separate settings for
/// separate moments: that one retries a connect the caller asked for, this one retries a session
/// the caller never asked to lose.
/// </para>
/// </remarks>
public class ReconnectOptions
{
    private int _maxAttempts = 5;
    private TimeSpan _initialDelay = TimeSpan.FromSeconds(1);
    private TimeSpan _maxDelay = TimeSpan.FromSeconds(30);
    private double _backoffMultiplier = 2.0;

    /// <summary>
    /// Gets or sets a value indicating whether a lost connection is reconnected automatically.
    /// Default is <c>false</c> — reconnect is opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets how many reconnect attempts are made before giving up. Default is 5.
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
                    nameof(MaxAttempts), value, "At least one reconnect attempt must be allowed.");
            }

            _maxAttempts = value;
        }
    }

    /// <summary>
    /// Gets or sets the wait before the <em>first</em> reconnect attempt. Default is 1 second.
    /// </summary>
    /// <remarks>
    /// There is always a wait before the first attempt, unlike
    /// <see cref="Communication.Transport.ConnectionRetryOptions"/>: at the instant a drop is
    /// detected the endpoint is, by definition, gone. A serial port that has just been unplugged
    /// has not finished disappearing from the OS yet, let alone come back.
    /// </remarks>
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
    /// Gets or sets the ceiling the backoff grows to. Default is 30 seconds.
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
    /// Gets or sets the factor each successive delay is multiplied by. Default is 2.0; 1.0 gives a
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
    /// Gets or sets a value indicating whether a stream that was running when the connection
    /// dropped is restarted once the session is back. Default is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Set to <c>false</c> to restore the channel configuration but leave the device idle — useful
    /// when the consumer wants to decide for itself whether resuming acquisition still makes sense
    /// after an outage of unknown length.
    /// </remarks>
    public bool ResumeStreaming { get; set; } = true;

    /// <summary>
    /// A policy that never reconnects. Identical to a default-constructed instance; named for
    /// callers that want to say so explicitly.
    /// </summary>
    public static ReconnectOptions Disabled => new();

    /// <summary>
    /// A reasonable enabled policy: five attempts, 1 s growing to 30 s, resuming an active stream.
    /// </summary>
    public static ReconnectOptions Default => new() { Enabled = true };

    /// <summary>
    /// A policy for links that blip briefly and often — six quick attempts inside about 10 seconds.
    /// </summary>
    public static ReconnectOptions Fast => new()
    {
        Enabled = true,
        MaxAttempts = 6,
        InitialDelay = TimeSpan.FromMilliseconds(500),
        MaxDelay = TimeSpan.FromSeconds(4),
        BackoffMultiplier = 1.5
    };

    /// <summary>
    /// A policy for unattended long runs: keeps trying for roughly ten minutes before giving up.
    /// </summary>
    public static ReconnectOptions Resilient => new()
    {
        Enabled = true,
        MaxAttempts = 15,
        InitialDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(60),
        BackoffMultiplier = 2.0
    };

    /// <summary>
    /// Calculates how long to wait before attempt <paramref name="attemptNumber"/>.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt number.</param>
    /// <returns>
    /// <see cref="InitialDelay"/> multiplied by <see cref="BackoffMultiplier"/> once per attempt
    /// already made, capped at <see cref="MaxDelay"/>. Always <see cref="TimeSpan.Zero"/> when
    /// <see cref="InitialDelay"/> is zero, however many attempts have been made.
    /// </returns>
    public TimeSpan CalculateDelay(int attemptNumber)
    {
        // Zero in, zero out. A caller who asked for immediate retries must get them at every
        // attempt number: multiplying zero by the growing backoff factor is still zero, right up
        // until that factor overflows Math.Pow to infinity, where 0 × ∞ is NaN and the arithmetic
        // below would hand back MaxDelay — the exact opposite of the configured policy.
        if (InitialDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // MaxDelay is a ceiling on every wait, including the first: a policy whose MaxDelay is
        // below its InitialDelay means what it says rather than exempting attempt 1.
        if (attemptNumber <= 1)
        {
            return InitialDelay < MaxDelay ? InitialDelay : MaxDelay;
        }

        var delayMs = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attemptNumber - 1);

        // Math.Pow overflows to +Infinity for a large enough attempt count. With a positive
        // InitialDelay the product is then +Infinity too, and Math.Min yields MaxDelay — the
        // intended cap. NaN is unreachable from here now that a zero InitialDelay returns above
        // and BackoffMultiplier rejects NaN, so this is a backstop against a future relaxation of
        // either, not a live path.
        if (double.IsNaN(delayMs))
        {
            return MaxDelay;
        }

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, MaxDelay.TotalMilliseconds));
    }
}
