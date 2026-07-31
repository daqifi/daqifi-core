using System.Collections.Concurrent;
using System.Diagnostics;

namespace Daqifi.Core.Device;

/// <summary>
/// Bounds how often a repeating background failure is raised as a
/// <see cref="DeviceErrorEventArgs"/>, so a systematic failure stays visible without storming the
/// subscriber (issue #378).
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for is a decode that throws on <em>every</em> frame. At a few kHz that
/// is thousands of raises per second — enough that a naive subscriber (a log sink, a UI marshal)
/// becomes the bottleneck and the "observability" feature degrades the streaming it was added to
/// explain. Collapsing repeats keeps the first report immediate and the rest cheap.
/// </para>
/// <para>
/// The policy, per bucket (<see cref="DeviceErrorSource"/> + exception type):
/// </para>
/// <list type="bullet">
/// <item><description>The first occurrence always passes, immediately.</description></item>
/// <item><description>
/// Later occurrences pass at most once per <see cref="Interval"/>; the ones in between are counted
/// and reported as <see cref="DeviceErrorEventArgs.SuppressedCount"/> on the next one that passes.
/// </description></item>
/// <item><description>
/// Buckets are per exception type, so a <em>new</em> kind of failure is never delayed behind an
/// ongoing storm of a different one.
/// </description></item>
/// </list>
/// <para>
/// Bucket count is capped at <see cref="MaxTrackedBuckets"/>; everything past the cap shares a
/// single overflow bucket. Exception types come from code rather than from the wire, so the cap is
/// unreachable in practice — it exists so this can never grow without bound.
/// </para>
/// </remarks>
internal sealed class DeviceErrorThrottle
{
    /// <summary>
    /// Default minimum spacing between raises of the same bucket.
    /// </summary>
    /// <remarks>
    /// Long enough that a per-frame failure at kHz rates collapses to a trickle, short enough that
    /// a human watching a log still sees the problem is ongoing rather than a single stale line.
    /// </remarks>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of distinct buckets tracked before everything else shares one.
    /// </summary>
    internal const int MaxTrackedBuckets = 32;

    private const string OverflowKey = "<overflow>";

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeSpan _interval;

    /// <summary>
    /// Initializes a new throttle.
    /// </summary>
    /// <param name="interval">
    /// Minimum spacing between raises of the same bucket. Defaults to <see cref="DefaultInterval"/>.
    /// <see cref="TimeSpan.Zero"/> disables collapsing entirely (every occurrence passes).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="interval"/> is negative.</exception>
    public DeviceErrorThrottle(TimeSpan? interval = null)
    {
        _interval = interval ?? DefaultInterval;
        if (_interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), _interval, "Throttle interval cannot be negative.");
        }
    }

    /// <summary>
    /// Gets the minimum spacing between raises of the same bucket.
    /// </summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// Decides whether this occurrence should be raised.
    /// </summary>
    /// <param name="source">The pipeline stage that failed.</param>
    /// <param name="error">The exception that was caught.</param>
    /// <param name="suppressedCount">
    /// When this returns <c>true</c>, the number of like occurrences collapsed since the previous
    /// raise (zero if none). Meaningless when it returns <c>false</c>.
    /// </param>
    /// <returns><c>true</c> to raise, <c>false</c> to collapse this occurrence into the count.</returns>
    public bool ShouldRaise(DeviceErrorSource source, Exception error, out int suppressedCount)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (_interval == TimeSpan.Zero)
        {
            suppressedCount = 0;
            return true;
        }

        var bucket = GetBucket($"{(int)source}:{error.GetType().FullName}");

        lock (bucket)
        {
            var now = Stopwatch.GetTimestamp();

            if (bucket.HasRaised && Stopwatch.GetElapsedTime(bucket.LastRaised, now) < _interval)
            {
                // Saturate rather than overflow: a run long enough to wrap int is already
                // "enormous", and a negative count would be worse than an inexact one.
                if (bucket.Suppressed < int.MaxValue)
                {
                    bucket.Suppressed++;
                }

                suppressedCount = 0;
                return false;
            }

            suppressedCount = bucket.Suppressed;
            bucket.Suppressed = 0;
            bucket.LastRaised = now;
            bucket.HasRaised = true;
            return true;
        }
    }

    /// <summary>
    /// Forgets all accumulated state, so the next occurrence of anything is raised immediately.
    /// </summary>
    /// <remarks>
    /// Called when a device connects: a reconnect is a new session, and its first failure should be
    /// reported at once rather than collapsed into a window opened by the previous session.
    /// </remarks>
    public void Reset() => _buckets.Clear();

    /// <summary>
    /// Resolves the bucket for a key, folding anything past <see cref="MaxTrackedBuckets"/> into a
    /// shared overflow bucket.
    /// </summary>
    /// <remarks>
    /// The count check is deliberately not atomic with the insert: racing callers may push the
    /// table a few entries past the cap. That is harmless — the cap exists to stop unbounded
    /// growth, not to be an exact quota — and paying for a lock on every background error to make
    /// it exact would be the wrong trade.
    /// </remarks>
    private Bucket GetBucket(string key)
    {
        if (_buckets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_buckets.Count >= MaxTrackedBuckets)
        {
            key = OverflowKey;
        }

        return _buckets.GetOrAdd(key, _ => new Bucket());
    }

    /// <summary>
    /// Per-key state. Mutated only under a lock on the instance itself.
    /// </summary>
    private sealed class Bucket
    {
        public long LastRaised;
        public bool HasRaised;
        public int Suppressed;
    }
}
