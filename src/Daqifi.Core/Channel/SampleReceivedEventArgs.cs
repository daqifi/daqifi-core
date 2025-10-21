namespace Daqifi.Core.Channel;

/// <summary>
/// Event arguments for when a channel receives a new data sample.
/// </summary>
public class SampleReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the data sample that was received.
    /// </summary>
    public IDataSample Sample { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SampleReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="sample">The data sample that was received.</param>
    public SampleReceivedEventArgs(IDataSample sample)
    {
        Sample = sample ?? throw new ArgumentNullException(nameof(sample));
    }
}
