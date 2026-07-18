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
            // finder-ordered "first wins" for deduplication.
            var results = await Task.WhenAll(_finders.Select(f => SafeDiscoverAsync(f, cancellationToken)))
                .ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<IDeviceInfo>();
            foreach (var device in results.SelectMany(r => r))
            {
                if (device != null && seen.Add(Identity(device)))
                {
                    unique.Add(device);
                    DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device));
                }
            }

            DiscoveryCompleted?.Invoke(this, EventArgs.Empty);
            return unique;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await DiscoverAsync(cts.Token).ConfigureAwait(false);
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
                Trace.WriteLine($"[{nameof(AllTransportsDeviceFinder)}] {finder.GetType().Name} discovery failed: {ex}");
                return Enumerable.Empty<IDeviceInfo>();
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
