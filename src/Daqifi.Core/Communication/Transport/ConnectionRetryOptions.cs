namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Configuration options for connection retry behavior with exponential backoff.
/// </summary>
public class ConnectionRetryOptions
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts. Default is 3.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial delay before the first retry attempt. Default is 1 second.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts. Default is 30 seconds.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the backoff multiplier for exponential backoff. Default is 2.0.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the connection timeout for each attempt. Default is 5 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

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

        var delay = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attemptNumber - 2);
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxDelay.TotalMilliseconds));
    }
}
