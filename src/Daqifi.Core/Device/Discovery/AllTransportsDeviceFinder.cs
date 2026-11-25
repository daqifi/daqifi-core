using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// An <see cref="IDeviceFinder"/> that fans a single discovery out across several transport
/// finders concurrently and returns one deduplicated device set — so "find any DAQiFi on WiFi or
/// USB" is a single call instead of the manual instantiate-both / run-both / concatenate / dedupe
/// dance every consumer otherwise repeats. <see cref="CreateDefault"/> covers WiFi broadcast +
/// serial; pass <see cref="MdnsDeviceFinder"/> to the constructor alongside them to add mDNS
/// browsing, which reaches devices on networks where broadcast does not (issue #183).
/// </summary>
/// <remarks>
/// Because <see cref="ContinuousDeviceFinder"/> wraps any <see cref="IDeviceFinder"/>, passing an
/// instance of this class to it yields deduplicated <b>continuous</b> discovery across all
/// transports for free. Deduplication reuses <see cref="ContinuousDeviceFinder.DefaultIdentity"/>
/// (issue #245): the same physical device reachable on two transports is intentionally kept as two
/// entries (two genuine ways to connect), while a device reported twice by one transport collapses
/// to one. A single transport finder throwing (e.g. WiFi discovery with no network) is logged and
/// skipped so the other transports still return results.
/// </remarks>
public sealed class AllTransportsDeviceFinder : IDeviceFinder, IBusyPortReporter, IPartialResultReporter
{
    private readonly IReadOnlyList<IDeviceFinder> _finders;
    private readonly Func<IDeviceInfo, string>? _identitySelector;
    private readonly bool _ownsFinders;
    private bool _disposed;

    // 1 when a transport answered its timeout by throwing, so the merged list lacks whatever it
    // would have found (see IPartialResultReporter).
    private int _transportTimedOut;

    /// <inheritdoc />
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <inheritdoc />
    public event EventHandler? DiscoveryCompleted;

    /// <summary>
    /// Initializes a new instance over the supplied transport finders. The caller retains
    /// ownership of the finders and is responsible for disposing them.
    /// </summary>
    /// <param name="finders">The transport finders to aggregate. Must be non-empty.</param>
    /// <param name="identitySelector">
    /// Optional per-device identity used for deduplication. When null (or it returns an
    /// empty key for a device), <see cref="ContinuousDeviceFinder.DefaultIdentity"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="finders"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="finders"/> is empty or contains null.</exception>
    public AllTransportsDeviceFinder(
        IEnumerable<IDeviceFinder> finders,
        Func<IDeviceInfo, string>? identitySelector = null)
        : this(finders, identitySelector, ownsFinders: false)
    {
    }

    private AllTransportsDeviceFinder(
        IEnumerable<IDeviceFinder> finders,
        Func<IDeviceInfo, string>? identitySelector,
        bool ownsFinders)
    {
        if (finders == null)
        {
            throw new ArgumentNullException(nameof(finders));
        }

        var list = finders.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one transport finder is required.", nameof(finders));
        }

        if (list.Any(f => f == null))
        {
            throw new ArgumentException("Transport finders must not contain null.", nameof(finders));
        }

        _finders = list;
        _identitySelector = identitySelector;
        _ownsFinders = ownsFinders;
    }

    /// <summary>
    /// Creates a finder over the default transports (WiFi + serial). The returned instance owns
    /// those finders and disposes them when it is disposed.
    /// </summary>
    /// <param name="identitySelector">Optional deduplication identity; see the constructor.</param>
    /// <returns>A new <see cref="AllTransportsDeviceFinder"/> over the default transports.</returns>
    public static AllTransportsDeviceFinder CreateDefault(Func<IDeviceInfo, string>? identitySelector = null)
    {
        var finders = new IDeviceFinder[] { new WifiDeviceFinder(), new SerialDeviceFinder() };
        return new AllTransportsDeviceFinder(finders, identitySelector, ownsFinders: true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded from whichever constituents report busy ports, because a composite that
    /// swallowed the signal would defeat the feature on the ONLY construction path most
    /// callers use. <see cref="ContinuousDeviceFinder"/> asks the finder it directly wraps,
    /// and the documented shape is to wrap this class (see <see cref="CreateDefault"/>) --
    /// so without this member the busy-port rescue silently never runs, and a device whose
    /// port the application itself is holding is reported lost. That is exactly the bug the
    /// rescue exists to prevent, so the seam has to carry it.
    ///
    /// Nesting works without special handling: this type now implements the interface, so a
    /// composite of composites is picked up by the same filter and aggregates recursively.
    /// </remarks>
    IReadOnlyCollection<BusyPort> IBusyPortReporter.TakeBusyPortsFromLastPass()
    {
        // ToList() per constituent, deliberately: taking is single-use, so EVERY reporter must
        // be drained on every call. A lazy chain that some caller short-circuited would leave
        // an untaken snapshot behind, to be handed out later as if it were current.
        var taken = new List<BusyPort>();
        foreach (var reporter in _finders.OfType<IBusyPortReporter>())
        {
            taken.AddRange(reporter.TakeBusyPortsFromLastPass());
        }

        return taken;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded for the same reason the busy-port report above is: <see cref="ContinuousDeviceFinder"/>
    /// asks only the finder it directly wraps, and the documented shape is to wrap this class.
    /// A composite that swallowed the signal would leave continuous discovery believing a
    /// truncated serial sweep was a full census of every transport, which is what drops a device
    /// the sweep never reached.
    ///
    /// The merged list is partial if ANY constituent's is: a device missing from it may be one
    /// that constituent never got to. EVERY reporter is drained on every call, because taking is
    /// single-use -- short-circuiting on the first true would leave a stale flag behind to be
    /// answered on a later pass that was actually complete.
    /// </remarks>
    bool IPartialResultReporter.TakeResultsWerePartialFromLastPass()
    {
        var partial = Interlocked.Exchange(ref _transportTimedOut, 0) == 1;
        foreach (var reporter in _finders.OfType<IPartialResultReporter>())
        {
            partial |= reporter.TakeResultsWerePartialFromLastPass();
        }

        return partial;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="timeout"/> goes to every transport finder unchanged, so null lets each one
    /// apply its own default. A transport that fails — or that breaks
    /// <see cref="IDeviceFinder.DiscoverAsync"/>'s contract by throwing on its timeout — is logged
    /// and skipped, so the others still return results.
    /// </remarks>
    public async Task<IReadOnlyList<IDeviceInfo>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // Checked here because a transport's own rejection would be skipped as a failed transport.
        DeviceFinderBase.ThrowIfInvalidTimeout(timeout);
        ThrowIfDisposed();

        Volatile.Write(ref _transportTimedOut, 0);
        try
        {
            // Fan out concurrently; results[i] corresponds to _finders[i], giving a stable,
            // finder-ordered "first wins" for deduplication.
            var results = await Task.WhenAll(
                    _finders.Select(f => SafeDiscoverAsync(f, timeout, cancellationToken)))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return MergeAndPublish(results);
        }
        finally
        {
            RaiseIsolated(() => DiscoveryCompleted?.Invoke(this, EventArgs.Empty), nameof(DiscoveryCompleted));
        }
    }

    /// <summary>
    /// Deduplicates the per-finder results (finder-ordered, first-wins) and publishes
    /// <see cref="DeviceDiscovered"/>, isolating subscriber exceptions so a throwing consumer
    /// callback cannot abort discovery.
    /// </summary>
    private IReadOnlyList<IDeviceInfo> MergeAndPublish(IEnumerable<IEnumerable<IDeviceInfo>> results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<IDeviceInfo>();
        foreach (var device in results.SelectMany(r => r))
        {
            if (device != null && seen.Add(Identity(device)))
            {
                unique.Add(device);
                RaiseIsolated(() => DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device)), nameof(DeviceDiscovered));
            }
        }

        return unique;
    }

    private static void RaiseIsolated(Action raise, string eventName)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            // Discovery outcome must not depend on consumer callback correctness (nor on the
            // logging path — SafeTrace swallows a throwing TraceListener). The line is composed
            // inside the lambda so a throwing Exception.ToString cannot escape this catch either.
            SafeTrace(() =>
                $"[{nameof(AllTransportsDeviceFinder)}] {eventName} subscriber threw: {ex}");
        }
    }

    private async Task<IReadOnlyList<IDeviceInfo>> SafeDiscoverAsync(
        IDeviceFinder finder,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await finder.DiscoverAsync(timeout, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's cancellation. DiscoverAsync throws it once every transport has ended,
            // so it surfaces as that even when another transport failed on the way out.
            return [];
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                Volatile.Write(ref _transportTimedOut, 1);
            }

            // One transport failing (e.g. WiFi with no network) must not sink the whole discovery.
            // Composed inside the lambda so a throwing Exception.ToString stays in the guard.
            SafeTrace(() =>
                $"[{nameof(AllTransportsDeviceFinder)}] {finder.GetType().Name} discovery failed: {ex}");
            return [];
        }
    }

    private string Identity(IDeviceInfo device)
    {
        if (_identitySelector != null)
        {
            var key = _identitySelector(device);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return ContinuousDeviceFinder.DefaultIdentity(device);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AllTransportsDeviceFinder));
        }
    }

    /// <summary>
    /// Disposes the transport finders this instance owns (only those created by
    /// <see cref="CreateDefault"/>; caller-supplied finders are left to the caller).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsFinders)
        {
            foreach (var finder in _finders)
            {
                finder.Dispose();
            }
        }
    }
}
