using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Shared lifecycle scaffolding for the concrete <see cref="IDeviceFinder"/>
/// implementations (<see cref="HidDeviceFinder"/>, <see cref="SerialDeviceFinder"/>,
/// <see cref="WiFiDeviceFinder"/>). Owns the discovery serialization semaphore, the
/// <see cref="DeviceDiscovered"/>/<see cref="DiscoveryCompleted"/> events and their
/// raisers, the disposed flag, and one consistent timeout/await/dispose policy so the
/// boilerplate can no longer drift apart across copies (issue #343).
/// </summary>
public abstract class DeviceFinderBase : IDeviceFinder, IDisposable
{
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Serializes concurrent discovery passes on this instance. Derived finders
    /// acquire it at the start of a discovery body and release it in a finally.
    /// </summary>
    protected SemaphoreSlim DiscoverySemaphore => _discoverySemaphore;

    /// <inheritdoc />
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <inheritdoc />
    public event EventHandler? DiscoveryCompleted;

    /// <inheritdoc />
    public abstract Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await DiscoverAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Raises the <see cref="DeviceDiscovered"/> event. Subscriber exceptions are
    /// isolated so a throwing consumer callback cannot abort the discovery pass or
    /// suppress the subsequent <see cref="DiscoveryCompleted"/> event.
    /// </summary>
    /// <param name="deviceInfo">The discovered device metadata.</param>
    protected virtual void OnDeviceDiscovered(IDeviceInfo deviceInfo)
    {
        RaiseIsolated(() => DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(deviceInfo)), nameof(DeviceDiscovered));
    }

    /// <summary>
    /// Raises the <see cref="DiscoveryCompleted"/> event. Subscriber exceptions are
    /// isolated so a throwing consumer callback cannot fault the discovery body.
    /// </summary>
    protected virtual void OnDiscoveryCompleted()
    {
        RaiseIsolated(() => DiscoveryCompleted?.Invoke(this, EventArgs.Empty), nameof(DiscoveryCompleted));
    }

    /// <summary>
    /// Invokes an event raiser, swallowing any subscriber exception so the discovery
    /// outcome never depends on consumer callback correctness. Mirrors the isolation
    /// guarantee <see cref="AllTransportsDeviceFinder"/> applies to the same events.
    /// </summary>
    private void RaiseIsolated(Action raise, string eventName)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            // Best-effort trace only; the logging path must not fault discovery either
            // (a throwing TraceListener is swallowed).
            try
            {
                System.Diagnostics.Trace.WriteLine($"[{GetType().Name}] {eventName} subscriber threw: {ex}");
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources owned by the finder. Overrides must call the base
    /// implementation so the shared discovery semaphore is disposed and the
    /// disposed flag is set exactly once.
    /// </summary>
    /// <param name="disposing">
    /// <c>true</c> when called from <see cref="Dispose()"/>; <c>false</c> from a finalizer.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _discoverySemaphore.Dispose();
        }

        _disposed = true;
    }
}
