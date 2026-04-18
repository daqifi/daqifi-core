using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

internal sealed class InMemoryLoggingSessionSource : ILoggingSessionSource
{
    private readonly List<ChannelDescriptor> _channels;
    private readonly List<SampleRow> _samples;

    public DateTime SessionStart { get; }

    public InMemoryLoggingSessionSource(
        IEnumerable<ChannelDescriptor> channels,
        IEnumerable<SampleRow> samples,
        DateTime? sessionStart = null)
    {
        _channels = channels.ToList();
        _samples = samples.OrderBy(s => s.TimestampTicks).ToList();
        SessionStart = sessionStart ?? DateTime.UtcNow;
    }

    public IReadOnlyList<ChannelDescriptor> GetChannels() => _channels;

    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_samples.Count);

    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var sample in _samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return sample;
            await Task.Yield();
        }
    }
}
