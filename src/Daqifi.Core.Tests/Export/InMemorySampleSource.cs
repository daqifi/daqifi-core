using Daqifi.Core.Export;

namespace Daqifi.Core.Tests.Export;

internal sealed class InMemorySampleSource : ISampleSource
{
    private readonly List<ChannelDescriptor> _channels;
    private readonly List<SampleRow> _samples;

    public InMemorySampleSource(
        IEnumerable<ChannelDescriptor> channels,
        IEnumerable<SampleRow> samples)
    {
        _channels = channels.ToList();
        _samples = samples.OrderBy(s => s.TimestampTicks).ToList();
    }

    public IReadOnlyList<ChannelDescriptor> Channels => _channels;

    public string? ValueUnit => null;

    public ValueTask<long?> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<long?>(_samples.Count);

    public async IAsyncEnumerable<SampleRow> StreamSamplesAsync(
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
