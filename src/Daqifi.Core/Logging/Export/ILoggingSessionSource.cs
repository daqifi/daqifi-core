namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Abstracts the data source for a logging session export, hiding storage details (EF Core, SQLite, in-memory, etc.)
/// from the exporter.
/// </summary>
public interface ILoggingSessionSource
{
    /// <summary>
    /// Gets the UTC start time of the logging session.
    /// </summary>
    DateTime SessionStart { get; }

    /// <summary>
    /// Returns the ordered list of channels present in this session.
    /// The order determines column order in the exported CSV.
    /// </summary>
    IReadOnlyList<ChannelDescriptor> GetChannels();

    /// <summary>
    /// Returns the total number of sample rows in this session, used for progress reporting.
    /// May return 0 if the count is unavailable.
    /// </summary>
    ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams all samples for the session in ascending <see cref="SampleRow.TimestampTicks"/> order.
    /// </summary>
    IAsyncEnumerable<SampleRow> StreamSamples(CancellationToken cancellationToken = default);
}
