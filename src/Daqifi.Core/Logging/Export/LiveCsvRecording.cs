using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// What a live CSV recording produced, and what it lost. A recording with silent drops is worse
/// than one that failed, so everything that did not reach the file is counted here rather than
/// swallowed.
/// </summary>
/// <param name="SampleCount">
/// Live samples read off the device stream, including any counted in
/// <paramref name="UnmappedSampleCount"/>.
/// </param>
/// <param name="RowCount">
/// CSV data lines written, not counting the header. A line is a timestamp, not a sample — every
/// channel decoded from one device frame shares that frame's timestamp and the exporter collapses
/// them into a single line. Describes a default export; an averaged one
/// (<see cref="CsvExportOptions.AverageWindow"/>) writes one line per window instead.
/// </param>
/// <param name="DroppedSampleCount">
/// Samples the device discarded during this recording because the recording could not keep up with
/// the incoming rate — <see cref="ILiveSampleSource.DroppedLiveSampleCount"/> measured across the
/// recording rather than since the device connected. Non-zero means the CSV has gaps; a larger
/// <c>bufferCapacity</c>, a slower stream rate, or a faster writer are the fixes.
/// <para>
/// The underlying counter is device-wide, so if another live consumer is enumerating the same
/// device while the recording runs, its drops are included here too. Nothing stops several
/// enumerations at once and the device reports drops as one health signal, so this is a delta over
/// a shared number rather than a per-recording one. With a single consumer — the normal case — it
/// is exactly this recording's losses.
/// </para>
/// </param>
/// <param name="UnmappedSampleCount">
/// Samples that arrived for a channel the recording had no column for — one enabled by another
/// caller after it started. Non-zero means the CSV is missing a channel the device was sending.
/// </param>
/// <param name="NonMonotonicSampleCount">
/// Samples whose timestamp went backwards relative to the one before. Expected to be zero; see
/// <see cref="LiveSampleSource.NonMonotonicSampleCount"/> for when it might not be.
/// </param>
public readonly record struct LiveCsvRecordingResult(
    long SampleCount,
    long RowCount,
    long DroppedSampleCount,
    long UnmappedSampleCount,
    long NonMonotonicSampleCount);

/// <summary>
/// Records a streaming device's live samples straight to CSV.
/// </summary>
public static class LiveCsvRecordingExtensions
{
    /// <summary>
    /// Streams the device's live samples through <see cref="CsvExporter"/> into
    /// <paramref name="writer"/> as they arrive, and reports what was written and what was lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "stream from the device and record it to a file" workflow, which Core could not
    /// do before: it shipped a live stream in one namespace and an exporter in another with nothing
    /// joining them, so every consumer that wanted a recording had to hand-roll the adapter (issue
    /// #639). Nothing is buffered — rows are written as frames decode, so a recording's memory does
    /// not grow with its length and a long run is not lost if the process dies partway.
    /// </para>
    /// <para>
    /// <b>The recorded channels are the ones enabled when the call starts.</b> A CSV cannot grow a
    /// column halfway down the file, so the column set is fixed up front; a channel enabled later
    /// shows up as <see cref="LiveCsvRecordingResult.UnmappedSampleCount"/> instead of as a new
    /// column. If no channel is enabled there is nothing to record, and the result is all zeros.
    /// </para>
    /// <para>
    /// <b>Ending a recording.</b> <paramref name="duration"/> is the clean stop: the window elapsing
    /// is how a timed recording finishes, not a failure, so the last frame is flushed, the file is
    /// complete, and the result is returned. <paramref name="cancellationToken"/> is the abort: it
    /// propagates as <see cref="OperationCanceledException"/> and the file is left as far as it got,
    /// because an acquisition cut short must not be indistinguishable from a complete one. This is
    /// the same split the live-capture path already draws. With neither, the recording runs until
    /// the device's stream ends — which it does when the device disconnects.
    /// </para>
    /// <para>
    /// Cancelling does <b>not</b> stop the device streaming; call
    /// <see cref="IStreamingDevice.StopStreaming"/> for that. Equally, this does not start the
    /// stream: enable the channels and call <see cref="IStreamingDevice.StartStreaming"/> first, or
    /// the recording waits for samples that are not coming.
    /// </para>
    /// <para>
    /// <paramref name="writer"/> is flushed once at the end but never disposed — the caller owns it,
    /// as with <see cref="CsvExporter.ExportAsync"/>. A caller who wants the file to stay current
    /// while a long recording runs should set <see cref="StreamWriter.AutoFlush"/> on it.
    /// </para>
    /// </remarks>
    /// <param name="device">
    /// The device to record. Must also implement <see cref="ILiveSampleSource"/>, which is what
    /// supplies the live samples; <see cref="DaqifiStreamingDevice"/> does.
    /// </param>
    /// <param name="writer">The destination. Not disposed by this method.</param>
    /// <param name="options">Export formatting; null uses <see cref="CsvExportOptions"/>'s defaults.</param>
    /// <param name="duration">
    /// How long to record for. Null records until the stream ends or the caller cancels.
    /// </param>
    /// <param name="bufferCapacity">
    /// The live stream's bounded-buffer capacity in samples; null uses
    /// <see cref="DaqifiStreamingDevice.DefaultLiveSampleBufferCapacity"/>. Raise it when
    /// <see cref="LiveCsvRecordingResult.DroppedSampleCount"/> comes back non-zero.
    /// </param>
    /// <param name="cancellationToken">Aborts the recording. See the remarks.</param>
    /// <returns>What the recording wrote, and what it lost.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> does not implement <see cref="ILiveSampleSource"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="duration"/> is not positive, or <paramref name="bufferCapacity"/> is less than 1.
    /// </exception>
    /// <exception cref="DeviceNotConnectedException">
    /// The device is not connected when the recording starts, or the connection was lost while it
    /// was running.
    /// </exception>
    public static async Task<LiveCsvRecordingResult> RecordLiveSamplesToCsvAsync(
        this IStreamingDevice device,
        TextWriter writer,
        CsvExportOptions? options = null,
        TimeSpan? duration = null,
        int? bufferCapacity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(writer);

        if (device is not ILiveSampleSource live)
        {
            throw new ArgumentException(
                $"Device '{device.Name}' does not supply live samples: it does not implement " +
                $"{nameof(ILiveSampleSource)}.",
                nameof(device));
        }

        if (duration is { } window && window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), window, "Recording duration must be greater than zero.");
        }

        // Validated here rather than left to the live stream, which defers it to the first
        // MoveNextAsync — from inside the exporter, where the argument is no longer the caller's.
        if (bufferCapacity is { } capacity && capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferCapacity), capacity, "Buffer capacity must be at least 1.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var channels = device.GetChannelsSnapshot().Where(c => c.IsEnabled).ToList();

        // The device's drop counter is cumulative across every enumeration it has served, so the
        // recording's own losses are the difference across it, not its absolute value.
        var droppedBefore = live.DroppedLiveSampleCount;

        using var durationCts = duration.HasValue
            ? new CancellationTokenSource(duration.Value)
            : null;
        var durationToken = durationCts?.Token ?? CancellationToken.None;

        var source = new LiveSampleSource(
            ReadUntilStoppedAsync(live, bufferCapacity, durationToken),
            channels,
            device.Name,
            device.Metadata.SerialNumber);

        await new CsvExporter()
            .ExportAsync(source, writer, options ?? new CsvExportOptions(), progress: null, cancellationToken)
            .ConfigureAwait(false);

        await writer.FlushAsync().ConfigureAwait(false);

        // With no columns the exporter returns without ever enumerating, so nothing was recorded and
        // nothing could have been dropped from it. The device counter still moves if another live
        // consumer is running, and attributing that to this call would contradict the documented
        // all-zeros result for a recording with no enabled channels.
        var dropped = channels.Count == 0 ? 0 : live.DroppedLiveSampleCount - droppedBefore;

        return new LiveCsvRecordingResult(
            source.SampleCount,
            source.RowCount,
            dropped,
            source.UnmappedSampleCount,
            source.NonMonotonicSampleCount);
    }

    /// <summary>
    /// Reads the live stream until it ends, the caller cancels, or the recording window elapses —
    /// treating only that last one as a normal end of the enumeration.
    /// </summary>
    /// <remarks>
    /// The window has to arrive as a cancellation because the read it interrupts is unbounded: a
    /// stream with no samples in it would otherwise sit in <c>MoveNextAsync</c> long past the
    /// window. Absorbing that cancellation <em>here</em>, instead of letting it reach the exporter,
    /// is what makes a timed recording end with a complete file: the exporter sees the enumeration
    /// finish normally, so it flushes the frame it was accumulating and writes its last line, which
    /// an <see cref="OperationCanceledException"/> torn through it would have discarded.
    /// </remarks>
    private static async IAsyncEnumerable<LiveSample> ReadUntilStoppedAsync(
        ILiveSampleSource live,
        int? bufferCapacity,
        CancellationToken durationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(durationToken, cancellationToken);

        // Disposed by hand rather than with `await using`, because ConfigureAwait(false) on an
        // IAsyncEnumerator yields a ConfiguredAsyncDisposable, which is no longer an enumerator.
        var enumerator = live
            .StreamSamplesAsync(linked.Token, bufferCapacity)
            .GetAsyncEnumerator(linked.Token);
        try
        {
            while (true)
            {
                LiveSample sample;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    sample = enumerator.Current;
                }
                catch (OperationCanceledException)
                    when (durationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                yield return sample;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
