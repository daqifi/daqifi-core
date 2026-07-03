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
    /// Gets the raw device value this sample was decoded from, or <c>null</c> when the device
    /// supplied an already-scaled value or the sample was not produced by the decode pipeline.
    /// </summary>
    public int? RawValue { get; init; }

    /// <summary>
    /// Gets the raw device timestamp (clock ticks) of the stream frame this sample was decoded
    /// from, or <c>null</c> when the sample was not produced from a stream frame.
    /// </summary>
    public uint? DeviceTimestamp { get; init; }

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
    /// Initializes a new instance of the <see cref="DataSample"/> class with decode metadata.
    /// </summary>
    /// <param name="timestamp">The host timestamp when the sample was taken.</param>
    /// <param name="value">The scaled value of the sample.</param>
    /// <param name="rawValue">The raw device value the sample was decoded from, or <c>null</c> if none.</param>
    /// <param name="deviceTimestamp">The raw device timestamp (clock ticks) of the source stream frame, or <c>null</c> if none.</param>
    public DataSample(DateTime timestamp, double value, int? rawValue, uint? deviceTimestamp)
    {
        Timestamp = timestamp;
        Value = value;
        RawValue = rawValue;
        DeviceTimestamp = deviceTimestamp;
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
