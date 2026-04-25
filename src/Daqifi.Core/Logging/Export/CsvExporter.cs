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
            throw new ArgumentOutOfRangeException(nameof(options), options.AverageWindow.Value,
                $"{nameof(CsvExportOptions.AverageWindow)} must be greater than zero.");

        var channels = source.GetChannels();
        if (channels.Count == 0)
            return;

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
        await writer.WriteAsync(timeHeader);
        foreach (var key in channelKeys)
        {
            await writer.WriteAsync(options.Delimiter);
            await writer.WriteAsync(key);
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
        sb.Append(FormatTimestamp(ticks, firstTicks, options.UseRelativeTime));

        var lookup = new Dictionary<string, double>(bucket.Count);
        foreach (var row in bucket)
            lookup[row.ChannelKey] = row.Value;

        foreach (var key in channelKeys)
        {
            sb.Append(options.Delimiter);
            if (lookup.TryGetValue(key, out var value))
                sb.Append(value.ToString("G", CultureInfo.InvariantCulture));
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
                sb.Clear();
                sb.Append(FormatTimestamp(lastTick, firstTicks.Value, options.UseRelativeTime));

                foreach (var key in channelKeys)
                {
                    sb.Append(options.Delimiter);
                    if (counts[key] > 0)
                        sb.Append((totals[key] / counts[key]).ToString("G", CultureInfo.InvariantCulture));
                }

                sb.AppendLine();
                await writer.WriteAsync(sb.ToString());

                foreach (var key in channelKeys)
                {
                    totals[key] = 0.0;
                    counts[key] = 0;
                }
                windowCount = 0;

                ReportProgress(progress, processed, totalSamples);
            }
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Formats a tick value as an absolute ISO 8601 string or relative seconds string.
    /// Ticks that are out of the valid <see cref="DateTime"/> range are rendered as <c>INVALID({ticks})</c>.
    /// </summary>
    private static string FormatTimestamp(long ticks, long firstTicks, bool useRelativeTime)
    {
        if (useRelativeTime)
            return ((ticks - firstTicks) / (double)TimeSpan.TicksPerSecond).ToString("F3", CultureInfo.InvariantCulture);

        return (ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            ? new DateTime(ticks).ToString("O")
            : $"INVALID({ticks})";
    }

    private static void ReportProgress(IProgress<int>? progress, int processed, int total)
    {
        if (progress == null || total <= 0)
            return;

        if (processed % 5000 == 0)
            progress.Report(Math.Min(99, (int)((double)processed / total * 100)));
    }
}
