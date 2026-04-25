namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Controls how <see cref="CsvExporter"/> formats a CSV export.
/// </summary>
public class CsvExportOptions
{
    /// <summary>
    /// Gets the field delimiter. Defaults to <c>","</c>.
    /// </summary>
    public string Delimiter { get; init; } = ",";

    /// <summary>
    /// When <see langword="true"/>, the time column contains seconds elapsed since the first sample
    /// (formatted to 3 decimal places). When <see langword="false"/>, it contains an ISO 8601
    /// round-trip timestamp (<c>DateTime.ToString("O")</c>).
    /// </summary>
    public bool UseRelativeTime { get; init; }

    /// <summary>
    /// When <see langword="null"/>, every sample is written as its own row (grouped by timestamp tick).
    /// When set to a positive integer <em>N</em>, samples are accumulated in a rolling window of <em>N</em>
    /// total samples (across all channels) and one averaged row is written per window.
    /// </summary>
    public int? AverageWindow { get; init; }
}
