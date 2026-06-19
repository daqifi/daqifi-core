using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Wraps an <see cref="IDeviceFinder"/> and turns its per-call discovery into a
/// continuous, stateful scan. The finder repeatedly runs discovery passes on a
/// configurable cadence, maintains a deduplicated live set of devices across
/// passes, and raises <see cref="DeviceDiscovered"/> when a device first appears
/// and <see cref="DeviceLost"/> when a device has been absent for
/// <see cref="ContinuousDiscoveryOptions.MissThreshold"/> consecutive passes. A UI
/// can bind directly to <see cref="Devices"/> plus these events instead of writing
/// its own polling loop and stale-removal logic.
/// </summary>
/// <remarks>
/// One instance wraps a single finder, so it represents one transport's scan
/// cadence and live set. To track multiple transports (WiFi, Serial, HID), create
/// one <see cref="ContinuousDeviceFinder"/> per finder — each can use its own
/// interval — and merge their events.
/// </remarks>
public class ContinuousDeviceFinder : IDisposable
{
    #region Private Types

    /// <summary>
    /// Tracks a device in the live set along with how many consecutive passes it
    /// has been missing.
    /// </summary>
    private sealed class TrackedDevice
    {
        public TrackedDevice(IDeviceInfo info)
        {
            Info = info;
        }

        /// <summary>The most recent metadata observed for the device.</summary>
        public IDeviceInfo Info { get; set; }

        /// <summary>Consecutive passes in which the device was not seen.</summary>
        public int MissCount { get; set; }
    }

    #endregion

    #region Private Fields

    private readonly IDeviceFinder _finder;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _passTimeout;
    private readonly int _missThreshold;
    private readonly Func<IDeviceInfo, string>? _identitySelector;
    private readonly bool _leaveInnerFinderOpen;

    private readonly Dictionary<string, TrackedDevice> _devices = new(StringComparer.Ordinal);
    private readonly object _devicesLock = new();
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _running;
    private volatile bool _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a device first enters the live set. Unlike the wrapped finder's
    /// own event, this fires only once per device until it is lost and rediscovered.
    /// </summary>
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Occurs when a device leaves the live set after being absent for
    /// <see cref="ContinuousDiscoveryOptions.MissThreshold"/> consecutive passes.
    /// </summary>
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;

    /// <summary>
    /// Occurs when a discovery pass throws. The scan loop continues after the failure;
    /// this event is for surfacing or logging the error.
    /// </summary>
    public event EventHandler<ContinuousDiscoveryErrorEventArgs>? ScanError;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousDeviceFinder"/> class.
    /// </summary>
    /// <param name="finder">The finder to run continuously. Required.</param>
    /// <param name="options">
    /// Scan cadence and stale-removal configuration. When null, defaults are used.
    /// The relevant values are captured at construction, so mutating the options object
    /// afterward has no effect.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="finder"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option value is out of range (non-positive pass timeout, negative interval, or
    /// a miss threshold below 1).
    /// </exception>
    public ContinuousDeviceFinder(IDeviceFinder finder, ContinuousDiscoveryOptions? options = null)
    {
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));

        options ??= new ContinuousDiscoveryOptions();

        if (options.PassTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PassTimeout, "PassTimeout must be greater than zero.");
        }

        if (options.Interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Interval, "Interval must not be negative.");
        }

        if (options.MissThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MissThreshold, "MissThreshold must be at least 1.");
        }

        _interval = options.Interval;
        _passTimeout = options.PassTimeout;
        _missThreshold = options.MissThreshold;
        _identitySelector = options.IdentitySelector;
        _leaveInnerFinderOpen = options.LeaveInnerFinderOpen;
    }

    #endregion

    #region Public Surface

    /// <summary>
    /// Gets a value indicating whether the continuous scan loop is running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Gets a point-in-time snapshot of the current live set of discovered devices.
    /// The returned list is a copy and is safe to enumerate from any thread.
    /// </summary>
    public IReadOnlyList<IDeviceInfo> Devices
    {
        get
        {
            lock (_devicesLock)
            {
                var snapshot = new List<IDeviceInfo>(_devices.Count);
                foreach (var tracked in _devices.Values)
                {
                    snapshot.Add(tracked.Info);
                }

                return snapshot;
            }
        }
    }

    /// <summary>
    /// Starts the continuous scan loop on a background task.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The finder has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The loop is already running.</exception>
    public void Start()
    {
        // Check disposed inside the lock so a concurrent Dispose (which sets _disposed under
        // the same lock) cannot slip in between the check and launching the loop.
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();

            if (_running)
            {
                throw new InvalidOperationException("Continuous discovery is already running.");
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = true;
            _loopTask = Task.Run(() => ScanLoopAsync(token));
        }
    }

    /// <summary>
    /// Stops the continuous scan loop and waits for it to stop, cancelling any in-flight pass.
    /// Safe to call when not running. Does not clear the live set.
    /// </summary>
    /// <returns>A task that completes once the loop has stopped.</returns>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        lock (_lifecycleLock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        cts?.Cancel();

        if (loopTask != null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the loop observes cancellation.
            }
        }

        cts?.Dispose();
    }

    #endregion

    #region Reconciliation

    /// <summary>
    /// Reconciles the live set against the devices returned by a single discovery pass:
    /// adds and announces newly seen devices, refreshes metadata for devices still present,
    /// and increments the miss count for absent devices — removing and announcing those that
    /// have now missed <see cref="ContinuousDiscoveryOptions.MissThreshold"/> consecutive passes.
    /// Exposed internally so the dedup/stale logic can be unit-tested without timing.
    /// </summary>
    /// <param name="passResults">Devices observed in the most recent pass.</param>
    internal void Reconcile(IReadOnlyCollection<IDeviceInfo> passResults)
    {
        var newlyDiscovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();

        lock (_devicesLock)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var device in passResults)
            {
                if (device == null)
                {
                    continue;
                }

                var key = GetIdentity(device);
                seen.Add(key);

                if (_devices.TryGetValue(key, out var tracked))
                {
                    // Already known — refresh metadata (IP, port, power state, etc. can
                    // change between passes) and clear the miss counter.
                    tracked.Info = device;
                    tracked.MissCount = 0;
                }
                else
                {
                    _devices[key] = new TrackedDevice(device);
                    newlyDiscovered.Add(device);
                }
            }

            List<string>? toRemove = null;
            foreach (var pair in _devices)
            {
                if (seen.Contains(pair.Key))
                {
                    continue;
                }

                var tracked = pair.Value;
                tracked.MissCount++;
                if (tracked.MissCount >= _missThreshold)
                {
                    (toRemove ??= new List<string>()).Add(pair.Key);
                    lost.Add(tracked.Info);
                }
            }

            if (toRemove != null)
            {
                foreach (var key in toRemove)
                {
                    _devices.Remove(key);
                }
            }
        }

        // Raise events outside the lock so subscriber callbacks cannot deadlock against
        // a concurrent Devices read or a subsequent Reconcile. Each callback is guarded
        // so one throwing subscriber neither suppresses sibling notifications nor kills
        // the continuous scan loop (matching the finders' "swallow subscriber exceptions"
        // contract); the exception is surfaced via ScanError instead.
        foreach (var device in newlyDiscovered)
        {
            SafeRaise(() => OnDeviceDiscovered(device));
        }

        foreach (var device in lost)
        {
            SafeRaise(() => OnDeviceLost(device));
        }
    }

    /// <summary>
    /// Computes the identity key for a device using the configured selector, or the
    /// default per-transport identity when no selector was supplied.
    /// </summary>
    private string GetIdentity(IDeviceInfo device)
    {
        if (_identitySelector != null)
        {
            var key = _identitySelector(device);

            // A null/empty key would collapse every such device onto one dictionary entry,
            // silently breaking dedup and DeviceLost. Fall back to the built-in per-transport
            // identity rather than corrupting the live set.
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return DefaultIdentity(device);
    }

    /// <summary>
    /// Computes a stable per-transport identity for a device. The connection type is
    /// combined with the most stable available discriminator for that transport, so the
    /// same physical device seen on two transports is tracked as two distinct entries.
    /// The discriminator preference per transport is:
    /// <list type="bullet">
    /// <item><description>WiFi — MAC address, then serial number, then IP address.</description></item>
    /// <item><description>Serial — serial number (survives COM-port reassignment), then port name.</description></item>
    /// <item><description>HID — device path (bootloaders may report empty/duplicate serials), then serial number.</description></item>
    /// </list>
    /// Each preference falls back to the device name as a last resort. Internal for testing.
    /// </summary>
    /// <param name="device">The device to identify.</param>
    /// <returns>A non-null identity key.</returns>
    internal static string DefaultIdentity(IDeviceInfo device)
    {
        string discriminator = device.ConnectionType switch
        {
            ConnectionType.WiFi =>
                !string.IsNullOrWhiteSpace(device.MacAddress) ? "mac:" + device.MacAddress!.ToLowerInvariant()
                : !string.IsNullOrWhiteSpace(device.SerialNumber) ? "sn:" + device.SerialNumber
                : device.IPAddress != null ? "ip:" + device.IPAddress
                : "name:" + (device.Name ?? string.Empty),

            ConnectionType.Serial =>
                !string.IsNullOrWhiteSpace(device.SerialNumber) ? "sn:" + device.SerialNumber
                : !string.IsNullOrWhiteSpace(device.PortName) ? "port:" + device.PortName
                : "name:" + (device.Name ?? string.Empty),

            ConnectionType.Hid =>
                !string.IsNullOrWhiteSpace(device.DevicePath) ? "path:" + device.DevicePath
                : !string.IsNullOrWhiteSpace(device.SerialNumber) ? "sn:" + device.SerialNumber
                : "name:" + (device.Name ?? string.Empty),

            _ =>
                !string.IsNullOrWhiteSpace(device.MacAddress) ? "mac:" + device.MacAddress!.ToLowerInvariant()
                : !string.IsNullOrWhiteSpace(device.DevicePath) ? "path:" + device.DevicePath
                : !string.IsNullOrWhiteSpace(device.PortName) ? "port:" + device.PortName
                : !string.IsNullOrWhiteSpace(device.SerialNumber) ? "sn:" + device.SerialNumber
                : "name:" + (device.Name ?? string.Empty),
        };

        return device.ConnectionType + "|" + discriminator;
    }

    #endregion

    #region Scan Loop

    /// <summary>
    /// Runs discovery passes until cancellation, reconciling the live set after each pass.
    /// Each pass runs under a cancellation token linked to the loop token and cancelled
    /// after <see cref="ContinuousDiscoveryOptions.PassTimeout"/>, so the timeout bounds the
    /// pass and a stop/dispose interrupts an in-flight pass promptly.
    /// </summary>
    private async Task ScanLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyCollection<IDeviceInfo>? results = null;

            using (var passCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                passCts.CancelAfter(_passTimeout);
                try
                {
                    var found = await _finder.DiscoverAsync(passCts.Token).ConfigureAwait(false);
                    results = found == null
                        ? Array.Empty<IDeviceInfo>()
                        : found as IReadOnlyCollection<IDeviceInfo> ?? found.ToList();
                }
                catch (OperationCanceledException)
                {
                    // Outer-token cancellation means stop/dispose — exit the loop. A pass-timeout
                    // cancellation (a finder that throws rather than returning what it collected) is
                    // treated as a skipped pass: leave results null so we don't fabricate losses.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    SafeScanError(ex);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Backstop: Reconcile guards individual subscriber callbacks, but a custom
            // identity selector could also throw. Neither must terminate the loop.
            if (results != null)
            {
                try
                {
                    Reconcile(results);
                }
                catch (Exception ex)
                {
                    SafeScanError(ex);
                }
            }

            if (!await DelayBetweenPassesAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Waits the configured inter-pass interval, returning false if cancellation was
    /// requested during (or before) the wait so the loop can exit promptly.
    /// </summary>
    private async Task<bool> DelayBetweenPassesAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_interval <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    #endregion

    #region Event Raisers

    /// <summary>
    /// Raises the <see cref="DeviceDiscovered"/> event.
    /// </summary>
    /// <param name="deviceInfo">The newly discovered device.</param>
    protected virtual void OnDeviceDiscovered(IDeviceInfo deviceInfo)
    {
        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(deviceInfo));
    }

    /// <summary>
    /// Raises the <see cref="DeviceLost"/> event.
    /// </summary>
    /// <param name="deviceInfo">The device that was lost.</param>
    protected virtual void OnDeviceLost(IDeviceInfo deviceInfo)
    {
        DeviceLost?.Invoke(this, new DeviceLostEventArgs(deviceInfo));
    }

    /// <summary>
    /// Raises the <see cref="ScanError"/> event.
    /// </summary>
    /// <param name="exception">The exception thrown by the failed pass.</param>
    protected virtual void OnScanError(Exception exception)
    {
        ScanError?.Invoke(this, new ContinuousDiscoveryErrorEventArgs(exception));
    }

    /// <summary>
    /// Invokes an event-raising action, routing any subscriber exception to
    /// <see cref="ScanError"/> so a single faulty handler can neither suppress sibling
    /// notifications nor terminate the scan loop.
    /// </summary>
    private void SafeRaise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            SafeScanError(ex);
        }
    }

    /// <summary>
    /// Raises <see cref="ScanError"/>, swallowing any exception thrown by a
    /// <see cref="ScanError"/> subscriber — error reporting must never terminate the loop.
    /// </summary>
    private void SafeScanError(Exception exception)
    {
        try
        {
            OnScanError(exception);
        }
        catch
        {
            // Intentionally swallowed: a throwing ScanError handler must not break the loop.
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ContinuousDeviceFinder));
        }
    }

    /// <summary>
    /// Stops the scan loop and releases resources. Unless
    /// <see cref="ContinuousDiscoveryOptions.LeaveInnerFinderOpen"/> was set, the wrapped
    /// finder is also disposed.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        // Set _disposed under the lock so it cannot race with Start (which checks it under
        // the same lock); also makes Dispose idempotent under concurrent calls.
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
            _running = false;
        }

        cts?.Cancel();

        // Wait for the loop to stop before disposing the inner finder, so no pass is in-flight
        // against a disposed finder. Cancelling the loop token also cancels the per-pass token,
        // so a well-behaved finder returns promptly; the bound (scaled to PassTimeout) only
        // guards against a finder that ignores cancellation, so Dispose can never hang. The loop
        // uses ConfigureAwait(false) throughout, so the wait cannot deadlock on a captured context.
        try
        {
            loopTask?.Wait(_passTimeout);
        }
        catch
        {
            // Ignore faults observed during shutdown.
        }

        cts?.Dispose();

        if (!_leaveInnerFinderOpen && _finder is IDisposable disposableFinder)
        {
            try
            {
                disposableFinder.Dispose();
            }
            catch
            {
                // Ignore finder disposal faults during shutdown.
            }
        }
    }

    #endregion
}
