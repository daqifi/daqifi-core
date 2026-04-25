namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Abstracts the data source for a CSV export, hiding storage details (EF Core, SQLite,
/// SD-card files, in-memory buffers, etc.) from the exporter.
/// </summary>
public interface ISampleSource
{
    /// <summary>
    /// Returns the ordered list of channels present in this source.
    /// The order determines column order in the exported CSV.
    /// </summary>
    IReadOnlyList<ChannelDescriptor> GetChannels();

    /// <summary>
    /// Returns the total number of sample rows, used for progress reporting.
    /// May return 0 if the count is unavailable.
    /// </summary>
    ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams all samples in ascending <see cref="SampleRow.TimestampTicks"/> order.
    /// </summary>
    IAsyncEnumerable<SampleRow> StreamSamples(CancellationToken cancellationToken = default);
}
