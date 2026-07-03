namespace Daqifi.Core.Channel;

/// <summary>
/// Event arguments for when a channel receives a new data sample.
/// </summary>
public class SampleReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the channel the sample was received on. This lets a single handler subscribed to
    /// multiple channels attribute each sample without capturing the channel per subscription.
    /// </summary>
    public IChannel Channel { get; }

    /// <summary>
    /// Gets the data sample that was received.
    /// </summary>
    public IDataSample Sample { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SampleReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="channel">The channel the sample was received on.</param>
    /// <param name="sample">The data sample that was received.</param>
    public SampleReceivedEventArgs(IChannel channel, IDataSample sample)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Sample = sample ?? throw new ArgumentNullException(nameof(sample));
    }
}
