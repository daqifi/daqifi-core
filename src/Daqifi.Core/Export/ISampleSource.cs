namespace Daqifi.Core.Export;

/// <summary>
/// Abstracts the data source for a CSV export, hiding storage details (EF Core, SQLite,
/// SD-card files, in-memory buffers, etc.) from the exporter.
/// </summary>
public interface ISampleSource
{
    /// <summary>
    /// Gets the ordered list of channels present in this source.
    /// The order determines column order in the exported CSV.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CsvExporter"/> reads this once per export.
    /// </para>
    /// <para>
    /// An implementation that discovers its channels — from a database, a file, a set of samples —
    /// rather than being handed them in a caller-chosen order should return them in
    /// <see cref="ChannelDescriptorComparer.Default"/> order: device name, then serial number, then
    /// channel name, with the channel name compared so that <c>AI2</c> precedes <c>AI10</c>. Saying
    /// only "ordered" left the rule to each implementation, and plain string ordering puts a
    /// device's <c>AI10</c> and <c>AI11</c> columns between <c>AI1</c> and <c>AI2</c> (issue #675).
    /// </para>
    /// <para>
    /// An implementation whose channels are supplied by the caller — <see cref="LiveStreamSampleSource"/>,
    /// for one — keeps the caller's order instead, since choosing the columns is the point of
    /// passing them in.
    /// </para>
    /// </remarks>
    IReadOnlyList<ChannelDescriptor> Channels { get; }

    /// <summary>
    /// The unit the analog values of <see cref="StreamSamplesAsync"/> are in, named in the exported
    /// column headers. <see langword="null"/> states no unit and writes the headers unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same samples left Core in two different units depending on the path — volts from a
    /// live recording, raw ADC counts from an SD-card log — and neither file said which, so a
    /// user plotting one against the other saw a difference of about a thousand times with
    /// nothing in the data to explain it (issue #903). A number with no unit is the defect; an
    /// unscaled number that says so is only inconvenient.
    /// </para>
    /// <para>
    /// This is the unit of the analog columns only. A digital column carries a port value, which
    /// has no unit, so it is never labelled.
    /// </para>
    /// <para>
    /// A source that does not know its unit must return <see langword="null"/> rather than guess.
    /// </para>
    /// </remarks>
    string? ValueUnit { get; }

    /// <summary>
    /// Returns the total number of sample rows, used for progress reporting, or
    /// <see langword="null"/> when the count is not known without reading the samples.
    /// </summary>
    ValueTask<long?> GetSampleCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams all samples in ascending <see cref="SampleRow.TimestampTicks"/> order.
    /// </summary>
    IAsyncEnumerable<SampleRow> StreamSamplesAsync(CancellationToken cancellationToken = default);
}
