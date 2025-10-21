namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a single data sample from a channel with timestamp and value.
/// </summary>
public class DataSample : IDataSample
{
    /// <summary>
    /// Gets the timestamp when the sample was taken.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets or sets the value of the sample.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataSample"/> class.
    /// </summary>
    public DataSample()
    {
        Timestamp = DateTime.UtcNow;
        Value = 0.0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataSample"/> class with specified values.
    /// </summary>
    /// <param name="timestamp">The timestamp when the sample was taken.</param>
    /// <param name="value">The value of the sample.</param>
    public DataSample(DateTime timestamp, double value)
    {
        Timestamp = timestamp;
        Value = value;
    }

    /// <summary>
    /// Returns a string representation of the data sample.
    /// </summary>
    /// <returns>A string containing the timestamp and value.</returns>
    public override string ToString()
    {
        return $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} - {Value}";
    }
}
