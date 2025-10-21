namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a single data sample from a channel.
/// </summary>
public interface IDataSample
{
    /// <summary>
    /// Gets the timestamp when the sample was taken.
    /// </summary>
    DateTime Timestamp { get; }

    /// <summary>
    /// Gets or sets the value of the sample.
    /// </summary>
    double Value { get; set; }
}
