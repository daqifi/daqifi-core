using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Shared lifecycle scaffolding for the concrete <see cref="IDeviceFinder"/>
/// implementations (<see cref="HidDeviceFinder"/>, <see cref="SerialDeviceFinder"/>,
/// <see cref="WifiDeviceFinder"/>, <see cref="MdnsDeviceFinder"/>). Owns the discovery
/// serialization semaphore, the <see cref="DeviceDiscovered"/>/<see cref="DiscoveryCompleted"/>
/// events and their raisers, the disposed flag, and the one timeout/cancellation/dispose policy
/// <see cref="IDeviceFinder.DiscoverAsync"/> documents, so it can no longer drift apart across
/// copies (issue #343). A finder supplies only the body of a pass (<see cref="RunPassAsync"/>).
/// </summary>
public abstract class DeviceFinderBase : IDeviceFinder, IPartialResultReporter
{
    /// <remarks>
    /// Deliberately never disposed. A pass that is running when the finder is disposed still
    /// holds it and releases it in its <c>finally</c>, and a second pass may already be waiting
    /// on it; disposing it underneath them made the first pass's release throw (replacing its
    /// results) and left the second waiting forever. Only <c>WaitAsync</c> and <c>Release</c> are
    /// used on it here, so there is no wait handle to free.
    /// </remarks>
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);

    // 0 = live, 1 = disposed. Written via Interlocked.Exchange so concurrent Dispose() calls
    // resolve to exactly one winner, and ThrowIfDisposed() starts throwing the moment disposal
    // begins (see Dispose(bool)).
    private int _disposed;

    // 1 when the most recent pass answered with less than it set out to probe. Written by the pass
    // and read by whoever consumes its result, so both go through Volatile/Interlocked.
    private int _lastPassWasPartial;

    /// <inheritdoc />
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <inheritdoc />
    public event EventHandler? DiscoveryCompleted;

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<IDeviceInfo>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfInvalidTimeout(timeout);
        ThrowIfDisposed();

        // One token tells the pass to end, for either reason; which reason it was is decided here
        // and nowhere else, so no finder can answer a timeout or a cancellation its own way.
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        passCts.CancelAfter(timeout ?? DefaultTimeout);

        var entered = false;
        try
        {
            await _discoverySemaphore.WaitAsync(passCts.Token).ConfigureAwait(false);
            entered = true;

            // This pass's own completeness, not the last one's. Reset even if nobody took the
            // previous value, so a pass that was cut short cannot vouch for the pass after it.
            Volatile.Write(ref _lastPassWasPartial, 0);

            var devices = await RunPassAsync(passCts.Token).ConfigureAwait(false);

            // A pass that collects as it goes returns normally when its token fires.
            cancellationToken.ThrowIfCancellationRequested();
            return devices;
        }
        catch (OperationCanceledException) when (passCts.IsCancellationRequested)
        {
            // Rethrown from here so the exception carries the caller's token, not the linked one.
            cancellationToken.ThrowIfCancellationRequested();

            // The timeout elapsed before the pass had anything to answer with — while it was
            // queued behind another pass, or in an enumeration that cannot return a part of
            // itself. "Found nothing" must not be read as "nothing is there".
            MarkPassPartial();
            return [];
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelling a pass can also break the transport under it. The caller asked for the
            // cancellation, so that is the answer.
            throw new OperationCanceledException("The discovery pass was cancelled.", ex, cancellationToken);
        }
        finally
        {
            try
            {
                OnDiscoveryCompleted();
            }
            finally
            {
                if (entered)
                {
                    _discoverySemaphore.Release();
                }
            }
        }
    }

    /// <summary>
    /// Runs the body of one discovery pass. Passes on one instance never overlap: the caller holds
    /// the finder's pass lock, and raises <see cref="DiscoveryCompleted"/> when this returns or throws.
    /// </summary>
    /// <param name="passToken">
    /// Fires when the pass must end, because its timeout elapsed or because it was cancelled. A pass
    /// that can return part of an answer returns what it has collected; one that cannot lets the
    /// <see cref="OperationCanceledException"/> out. <see cref="DiscoverAsync"/> turns either into
    /// what the caller asked for.
    /// </param>
    /// <returns>The devices found.</returns>
    protected abstract Task<IReadOnlyList<IDeviceInfo>> RunPassAsync(CancellationToken passToken);

    /// <summary>
    /// How long a pass runs when the caller gives no timeout: to its natural end, unless a finder
    /// that has none says otherwise.
    /// </summary>
    private protected virtual TimeSpan DefaultTimeout => Timeout.InfiniteTimeSpan;

    /// <summary>
    /// <see cref="DefaultTimeout"/> for a finder that only listens, and so would never return.
    /// </summary>
    private protected static readonly TimeSpan DefaultListenWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records that the running pass is answering with less than it set out to probe
    /// (<see cref="IPartialResultReporter"/>).
    /// </summary>
    private protected void MarkPassPartial() => Volatile.Write(ref _lastPassWasPartial, 1);

    /// <summary>
    /// Ends a pass whose transport failed as a whole: throws <paramref name="failure"/> when nothing
    /// was found, because an empty list would read as "nothing is there", and otherwise records that
    /// what was found is only part of the answer.
    /// </summary>
    private protected void EndFailedPass(Exception failure, int devicesFound)
    {
        if (devicesFound == 0)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        MarkPassPartial();
    }

    /// <inheritdoc />
    bool IPartialResultReporter.TakeResultsWerePartialFromLastPass()
        => Interlocked.Exchange(ref _lastPassWasPartial, 0) == 1;

    /// <summary>
    /// Rejects a timeout no pass could honour, before anything is started.
    /// </summary>
    internal static void ThrowIfInvalidTimeout(TimeSpan? timeout)
    {
        if (timeout is not { } value || value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        // The upper limit is CancellationTokenSource.CancelAfter's.
        if (value < TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                value,
                "Timeout must be zero or positive, or Timeout.InfiniteTimeSpan, and no longer than a timer can run.");
        }
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
            // Discovery outcome must not depend on consumer callback correctness (nor on the
            // logging path — SafeTrace swallows a throwing TraceListener). The line is composed
            // inside the guard: `ex` came from the subscriber, so a throwing ToString must not
            // escape this catch either.
            SafeTrace(() => $"[{GetType().Name}] {eventName} subscriber threw: {ex}");
        }
    }

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
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
    /// implementation so the disposed flag is set exactly once.
    /// </summary>
    /// <param name="disposing">
    /// <c>true</c> when called from <see cref="Dispose()"/>; <c>false</c> from a finalizer.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        // The first caller to flip the flag wins, and a later DiscoverAsync fails
        // ThrowIfDisposed() with the finder-named ObjectDisposedException. The discovery
        // semaphore is left alone; see _discoverySemaphore.
        Interlocked.Exchange(ref _disposed, 1);
    }
}
