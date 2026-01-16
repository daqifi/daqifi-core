namespace Daqifi.Core.Device;

/// <summary>
/// Represents the result of processing a device timestamp.
/// Contains the calculated system timestamp and metadata about the processing.
/// </summary>
public sealed class TimestampResult
{
    /// <summary>
    /// Gets the calculated system timestamp based on the device timestamp
    /// and the elapsed time since the last message.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Gets a value indicating whether a uint32 rollover was detected.
    /// Rollover occurs when the device timestamp counter wraps from uint.MaxValue back to 0.
    /// </summary>
    public bool WasRollover { get; }

    /// <summary>
    /// Gets the number of clock cycles between this message and the previous message.
    /// This value accounts for rollover if one was detected.
    /// </summary>
    public uint ClockCyclesBetweenMessages { get; }

    /// <summary>
    /// Gets the calculated time in seconds between this message and the previous message.
    /// This is derived from <see cref="ClockCyclesBetweenMessages"/> multiplied by the tick period.
    /// </summary>
    public double SecondsBetweenMessages { get; }

    /// <summary>
    /// Gets a value indicating whether this is the first message for this device
    /// in the current session. When true, the timestamp is the current system time.
    /// </summary>
    public bool IsFirstMessage { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimestampResult"/> class.
    /// </summary>
    /// <param name="timestamp">The calculated system timestamp.</param>
    /// <param name="wasRollover">Whether a rollover was detected.</param>
    /// <param name="clockCyclesBetweenMessages">The number of clock cycles between messages.</param>
    /// <param name="secondsBetweenMessages">The time in seconds between messages.</param>
    /// <param name="isFirstMessage">Whether this is the first message in the session.</param>
    public TimestampResult(
        DateTime timestamp,
        bool wasRollover,
        uint clockCyclesBetweenMessages,
        double secondsBetweenMessages,
        bool isFirstMessage)
    {
        Timestamp = timestamp;
        WasRollover = wasRollover;
        ClockCyclesBetweenMessages = clockCyclesBetweenMessages;
        SecondsBetweenMessages = secondsBetweenMessages;
        IsFirstMessage = isFirstMessage;
    }

    /// <summary>
    /// Creates a result for the first message in a session.
    /// </summary>
    /// <param name="timestamp">The current system timestamp.</param>
    /// <param name="deviceTimestamp">The device timestamp.</param>
    /// <returns>A new <see cref="TimestampResult"/> for the first message.</returns>
    internal static TimestampResult CreateFirstMessage(DateTime timestamp, uint deviceTimestamp)
    {
        return new TimestampResult(
            timestamp: timestamp,
            wasRollover: false,
            clockCyclesBetweenMessages: 0,
            secondsBetweenMessages: 0,
            isFirstMessage: true);
    }
}
