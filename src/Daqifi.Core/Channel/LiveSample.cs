namespace Daqifi.Core.Channel
{
    /// <summary>
    /// A single decoded live sample paired with the channel that produced it — the element type of
    /// <see cref="Daqifi.Core.Device.DaqifiStreamingDevice.StreamSamplesAsync"/>. Mirrors the
    /// pull-based element idiom the offline paths already use (SD-card log entries, export sample
    /// rows), so a live consumer can <c>await foreach</c> device-wide samples and attribute each to
    /// its channel without a hand-rolled event/queue bridge.
    /// </summary>
    /// <param name="Channel">The channel the sample was decoded for.</param>
    /// <param name="Sample">The decoded sample (host timestamp, scaled value, raw ADC count, device tick).</param>
    public sealed record LiveSample(IChannel Channel, IDataSample Sample);
}
