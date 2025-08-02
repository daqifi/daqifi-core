namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Provides data for message consumer error events.
/// </summary>
public class MessageConsumerErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the MessageConsumerErrorEventArgs class.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    /// <param name="rawData">The raw data being processed when the error occurred, if available.</param>
    public MessageConsumerErrorEventArgs(Exception error, byte[]? rawData = null)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
        RawData = rawData;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the error that occurred.
    /// </summary>
    public Exception Error { get; }

    /// <summary>
    /// Gets the raw data being processed when the error occurred, if available.
    /// </summary>
    public byte[]? RawData { get; }

    /// <summary>
    /// Gets the timestamp when the error occurred.
    /// </summary>
    public DateTime Timestamp { get; }
}