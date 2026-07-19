using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery
{
    /// <summary>
    /// An <see cref="IDeviceFinder"/> that fans a single discovery out across several transport
    /// finders (WiFi + serial today, mDNS/#183 later) concurrently and returns one deduplicated
    /// device set — so "find any DAQiFi on WiFi or USB" is a single call instead of the manual
    /// instantiate-both / run-both / concatenate / dedupe dance every consumer otherwise repeats.
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
    public sealed class AllTransportsDeviceFinder : IDeviceFinder, IDisposable
    {
        private readonly IReadOnlyList<IDeviceFinder> _finders;
        private readonly Func<IDeviceInfo, string>? _identitySelector;
        private readonly bool _ownsFinders;
        private bool _disposed;

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
            var finders = new IDeviceFinder[] { new WiFiDeviceFinder(), new SerialDeviceFinder() };
            return new AllTransportsDeviceFinder(finders, identitySelector, ownsFinders: true);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // Fan out concurrently; results[i] corresponds to _finders[i], giving a stable,
            // finder-ordered "first wins" for deduplication. A caller-supplied token that fires
            // surfaces as OperationCanceledException (SafeDiscoverAsync rethrows it).
            var results = await Task.WhenAll(_finders.Select(f => SafeDiscoverAsync(f, cancellationToken)))
                .ConfigureAwait(false);

            return MergeAndPublish(results);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
        {
            ThrowIfDisposed();

            // Delegate to each finder's own timeout overload so a timeout is a normal end-of-pass
            // (return what was found), matching WiFiDeviceFinder/SerialDeviceFinder — rather than
            // routing through the cancellation path, where a finder observing the timeout token
            // would throw OperationCanceledException and skip DiscoveryCompleted.
            var results = await Task.WhenAll(_finders.Select(f => SafeDiscoverAsync(f, timeout)))
                .ConfigureAwait(false);

            return MergeAndPublish(results);
        }

        /// <summary>
        /// Deduplicates the per-finder results (finder-ordered, first-wins) and publishes the
        /// <see cref="DeviceDiscovered"/> / <see cref="DiscoveryCompleted"/> events, isolating
        /// subscriber exceptions so a throwing consumer callback cannot abort discovery.
        /// </summary>
        private IEnumerable<IDeviceInfo> MergeAndPublish(IEnumerable<IEnumerable<IDeviceInfo>> results)
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

            RaiseIsolated(() => DiscoveryCompleted?.Invoke(this, EventArgs.Empty), nameof(DiscoveryCompleted));
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
                // logging path — SafeTrace swallows a throwing TraceListener).
                SafeTrace($"[{nameof(AllTransportsDeviceFinder)}] {eventName} subscriber threw: {ex}");
            }
        }

        private async Task<IEnumerable<IDeviceInfo>> SafeDiscoverAsync(IDeviceFinder finder, CancellationToken cancellationToken)
        {
            try
            {
                return await finder.DiscoverAsync(cancellationToken).ConfigureAwait(false)
                    ?? Enumerable.Empty<IDeviceInfo>();
            }
            catch (OperationCanceledException)
            {
                // Caller-requested cancellation must surface, not be swallowed as a transport failure.
                throw;
            }
            catch (Exception ex)
            {
                // One transport failing (e.g. WiFi with no network) must not sink the whole discovery.
                SafeTrace($"[{nameof(AllTransportsDeviceFinder)}] {finder.GetType().Name} discovery failed: {ex}");
                return Enumerable.Empty<IDeviceInfo>();
            }
        }

        private async Task<IEnumerable<IDeviceInfo>> SafeDiscoverAsync(IDeviceFinder finder, TimeSpan timeout)
        {
            try
            {
                return await finder.DiscoverAsync(timeout).ConfigureAwait(false)
                    ?? Enumerable.Empty<IDeviceInfo>();
            }
            catch (Exception ex)
            {
                // Timeout is a normal end-of-pass here (no caller token to honor); any failure —
                // timeout, socket error, disposed resource — must not sink the other transports'
                // results. Message stays generic since the cause is not necessarily the timeout.
                SafeTrace($"[{nameof(AllTransportsDeviceFinder)}] {finder.GetType().Name} discovery failed during a timed pass: {ex}");
                return Enumerable.Empty<IDeviceInfo>();
            }
        }

        private static void SafeTrace(string message)
        {
            try
            {
                Trace.WriteLine(message);
            }
            catch
            {
                // Best-effort logging: a misbehaving TraceListener must never affect discovery.
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
                foreach (var finder in _finders.OfType<IDisposable>())
                {
                    finder.Dispose();
                }
            }
        }
    }
}
