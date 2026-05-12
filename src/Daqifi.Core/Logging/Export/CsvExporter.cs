using System.Globalization;
using System.Text;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Exports timestamped channel samples to CSV format, writing to any <see cref="TextWriter"/>.
/// Storage concerns are delegated to <see cref="ISampleSource"/>; this class
/// contains only row-formatting and timestamp logic.
/// </summary>
public class CsvExporter
{
    /// <summary>
    /// Exports the samples provided by <paramref name="source"/> as CSV to <paramref name="writer"/>.
    /// </summary>
    /// <param name="source">The data source being exported.</param>
    /// <param name="writer">The destination writer. The caller is responsible for disposing it.</param>
    /// <param name="options">Export formatting options.</param>
    /// <param name="progress">
    /// Optional progress sink that receives values in the range [0, 100].
    /// Reported periodically as samples are processed; always reports 100 on completion.
    /// </param>
    /// <param name="cancellationToken">Token that aborts the export mid-stream.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="CsvExportOptions.AverageWindow"/> is set to a value less than or equal to zero.
    /// </exception>
    public async Task ExportAsync(
        ISampleSource source,
        TextWriter writer,
        CsvExportOptions options,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.AverageWindow.HasValue && options.AverageWindow.Value <= 0)
            throw new ArgumentOutOfRangeException(
                $"{nameof(options)}.{nameof(CsvExportOptions.AverageWindow)}",
                options.AverageWindow.Value,
                $"{nameof(CsvExportOptions.AverageWindow)} must be greater than zero.");

        // The double-quote is reserved as the RFC 4180 quoting character used by
        // EscapeCsvField, so allowing it as the delimiter would produce ambiguous,
        // unparseable output. Newlines would split fields across rows. Multi-char
        // / empty delimiters can't be handled by single-character splitting either.
        if (string.IsNullOrEmpty(options.Delimiter)
            || options.Delimiter.Length != 1
            || options.Delimiter == "\""
            || options.Delimiter == "\r"
            || options.Delimiter == "\n")
        {
            throw new ArgumentException(
                $"Delimiter must be a single character that is not a newline or double-quote (got '{options.Delimiter}').",
                $"{nameof(options)}.{nameof(CsvExportOptions.Delimiter)}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var channels = source.GetChannels();
        if (channels.Count == 0)
        {
            // Always finalize progress so callers (e.g. UI progress bars) don't
            // stall at <100% when the export is a no-op.
            progress?.Report(100);
            return;
        }

        var channelKeys = channels.Select(c => c.Key).ToList();

        await WriteHeaderAsync(writer, channelKeys, options);

        var totalSamples = progress != null
            ? await source.GetSampleCountAsync(cancellationToken)
            : 0;

        if (options.AverageWindow.HasValue)
            await ExportAveragedAsync(source, writer, options, channelKeys, totalSamples, progress, cancellationToken);
        else
            await ExportAllSamplesAsync(source, writer, options, channelKeys, totalSamples, progress, cancellationToken);
    }

    private static async Task WriteHeaderAsync(TextWriter writer, List<string> channelKeys, CsvExportOptions options)
    {
        var timeHeader = options.UseRelativeTime ? "Relative Time (s)" : "Time";
        await writer.WriteAsync(EscapeCsvField(timeHeader, options.Delimiter));
        foreach (var key in channelKeys)
        {
            await writer.WriteAsync(options.Delimiter);
            await writer.WriteAsync(EscapeCsvField(key, options.Delimiter));
        }
        await writer.WriteLineAsync();
    }

    private static async Task ExportAllSamplesAsync(
        ISampleSource source,
        TextWriter writer,
        CsvExportOptions options,
        List<string> channelKeys,
        int totalSamples,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(4 * 1024);
        long? firstTicks = null;
        long? currentTick = null;
        var bucket = new List<SampleRow>();
        var processed = 0;

        await foreach (var sample in source.StreamSamples(cancellationToken))
        {
            firstTicks ??= sample.TimestampTicks;

            if (currentTick.HasValue && sample.TimestampTicks != currentTick.Value)
            {
                await WriteTimestampRowAsync(writer, sb, bucket, channelKeys, firstTicks.Value, options);
                processed += bucket.Count;
                ReportProgress(progress, processed, totalSamples);
                bucket.Clear();
            }

            currentTick = sample.TimestampTicks;
            bucket.Add(sample);
        }

        if (bucket.Count > 0)
            await WriteTimestampRowAsync(writer, sb, bucket, channelKeys, firstTicks!.Value, options);

        progress?.Report(100);
    }

    private static async Task WriteTimestampRowAsync(
        TextWriter writer,
        StringBuilder sb,
        List<SampleRow> bucket,
        List<string> channelKeys,
        long firstTicks,
        CsvExportOptions options)
    {
        var ticks = bucket[0].TimestampTicks;
        sb.Clear();
        // Data fields use formulaSafe=false: timestamps and numeric values are
        // internally generated; their leading '-' is a sign on negative numbers,
        // not a formula char.
        sb.Append(EscapeCsvField(
            FormatTimestamp(ticks, firstTicks, options.UseRelativeTime),
            options.Delimiter, formulaSafe: false));

        var lookup = new Dictionary<string, double>(bucket.Count);
        foreach (var row in bucket)
            lookup[row.ChannelKey] = row.Value;

        foreach (var key in channelKeys)
        {
            sb.Append(options.Delimiter);
            if (lookup.TryGetValue(key, out var value))
                sb.Append(EscapeCsvField(
                    value.ToString("G", CultureInfo.InvariantCulture),
                    options.Delimiter, formulaSafe: false));
        }

        sb.AppendLine();
        await writer.WriteAsync(sb.ToString());
    }

    private static async Task ExportAveragedAsync(
        ISampleSource source,
        TextWriter writer,
        CsvExportOptions options,
        List<string> channelKeys,
        int totalSamples,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var window = options.AverageWindow!.Value;
        var totals = channelKeys.ToDictionary(k => k, _ => 0.0);
        var counts = channelKeys.ToDictionary(k => k, _ => 0);
        var sb = new StringBuilder(4 * 1024);
        long? firstTicks = null;
        long lastTick = 0;
        var windowCount = 0;
        var processed = 0;

        await foreach (var sample in source.StreamSamples(cancellationToken))
        {
            firstTicks ??= sample.TimestampTicks;
            lastTick = sample.TimestampTicks;

            if (totals.ContainsKey(sample.ChannelKey))
            {
                totals[sample.ChannelKey] += sample.Value;
                counts[sample.ChannelKey]++;
            }

            windowCount++;
            processed++;

            if (windowCount >= window)
            {
                await WriteAveragedRowAsync(writer, sb, channelKeys, totals, counts, lastTick, firstTicks.Value, options);

                foreach (var key in channelKeys)
                {
                    totals[key] = 0.0;
                    counts[key] = 0;
                }
                windowCount = 0;

                ReportProgress(progress, processed, totalSamples);
            }
        }

        // Flush any trailing samples that didn't fill a complete window.
        if (windowCount > 0 && firstTicks.HasValue)
            await WriteAveragedRowAsync(writer, sb, channelKeys, totals, counts, lastTick, firstTicks.Value, options);

        progress?.Report(100);
    }

    private static async Task WriteAveragedRowAsync(
        TextWriter writer,
        StringBuilder sb,
        List<string> channelKeys,
        Dictionary<string, double> totals,
        Dictionary<string, int> counts,
        long lastTick,
        long firstTicks,
        CsvExportOptions options)
    {
        sb.Clear();
        sb.Append(EscapeCsvField(
            FormatTimestamp(lastTick, firstTicks, options.UseRelativeTime),
            options.Delimiter, formulaSafe: false));

        foreach (var key in channelKeys)
        {
            sb.Append(options.Delimiter);
            if (counts[key] > 0)
                sb.Append(EscapeCsvField(
                    (totals[key] / counts[key]).ToString("G", CultureInfo.InvariantCulture),
                    options.Delimiter, formulaSafe: false));
        }

        sb.AppendLine();
        await writer.WriteAsync(sb.ToString());
    }

    /// <summary>
    /// Formats a tick value as an absolute ISO 8601 string or relative seconds string.
    /// Ticks that are out of the valid <see cref="DateTime"/> range are rendered as <c>INVALID({ticks})</c>
    /// in both modes. ticks==0 (DateTime.MinValue, 0001-01-01 00:00:00) is a legal
    /// value and IS rendered through the formatter; only negative ticks are invalid.
    /// </summary>
    private static string FormatTimestamp(long ticks, long firstTicks, bool useRelativeTime)
    {
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            return $"INVALID({ticks})";

        if (useRelativeTime)
            return ((ticks - firstTicks) / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture);

        return new DateTime(ticks).ToString("O");
    }

    /// <summary>
    /// RFC 4180 quoting + optional spreadsheet formula-injection neutralization.
    /// </summary>
    /// <param name="value">The field value to escape.</param>
    /// <param name="delimiter">The current CSV delimiter (single character, validated by caller).</param>
    /// <param name="formulaSafe">
    /// When true (default — header fields where channel names are user-controlled),
    /// prefix a literal <c>'</c> on values whose first non-whitespace character is
    /// <c>=</c>, <c>+</c>, <c>-</c>, or <c>@</c> so spreadsheet apps don't evaluate
    /// the field as a formula. When false (data fields — internally generated
    /// timestamps and numeric values), formula mitigation is skipped so legitimate
    /// negative numbers like <c>-1.23</c> aren't clobbered into <c>'-1.23</c>.
    /// </param>
    /// <returns>The escaped field, ready to write between delimiters.</returns>
    private static string EscapeCsvField(string value, string delimiter, bool formulaSafe = true)
    {
        if (formulaSafe && !string.IsNullOrEmpty(value))
        {
            // Skip ALL Unicode whitespace, not just ' ' and '\t'. CSV
            // formula-injection PoCs use NBSP (U+00A0), thin spaces, line
            // separator (U+2028), etc. before '=' to evade trim-based
            // checks; spreadsheets still treat the resulting cell as a
            // formula. char.IsWhiteSpace covers the full Unicode set.
            var i = 0;
            while (i < value.Length && char.IsWhiteSpace(value[i]))
                i++;
            if (i < value.Length && "=+-@".IndexOf(value[i]) >= 0)
            {
                value = "'" + value;
            }
        }

        var delimChar = delimiter[0];
        var mustQuote = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == delimChar || c == '"' || c == '\r' || c == '\n')
            {
                mustQuote = true;
                break;
            }
        }
        if (mustQuote)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private static void ReportProgress(IProgress<int>? progress, int processed, int total)
    {
        if (progress == null || total <= 0)
            return;

        if (processed % 5000 == 0)
            progress.Report(Math.Min(99, (int)((double)processed / total * 100)));
    }
}
