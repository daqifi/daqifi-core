using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Interface for device discovery mechanisms.
/// </summary>
public interface IDeviceFinder : IDisposable
{
    /// <summary>
    /// Occurs when a device is discovered.
    /// </summary>
    event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Occurs once at the end of every discovery pass, whichever way the pass ended: it finished,
    /// its timeout elapsed, it was cancelled, or it failed.
    /// </summary>
    event EventHandler? DiscoveryCompleted;

    /// <summary>
    /// Runs one discovery pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every finder ends a pass in one of these ways:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>It finishes</b> — returns the devices found.</description></item>
    /// <item><description><b><paramref name="timeout"/> elapses</b> — a normal end, not an error.
    /// Returns the devices found so far, which may be none. Running out of time is never itself a reason to throw.</description></item>
    /// <item><description><b><paramref name="cancellationToken"/> is cancelled</b> — throws
    /// <see cref="OperationCanceledException"/>, whatever had been found. Devices already announced
    /// through <see cref="DeviceDiscovered"/> stay announced. When the token is cancelled and the
    /// timeout has elapsed too, the cancellation wins.</description></item>
    /// <item><description><b>The transport cannot be scanned at all</b> — throws the transport's own
    /// exception: the ports could not be enumerated, say, or no network interface could be queried. A
    /// fault confined to one port or one network interface only skips that port or interface, and a
    /// host with none to scan finds nothing.</description></item>
    /// </list>
    /// <para>
    /// An empty result means "nothing answered within the budget you gave", not
    /// "no device is attached". A transport may need to finish releasing resources
    /// from a previous pass that ended by timeout or cancellation — see
    /// <see cref="SerialDeviceFinder"/>, which absorbs that wait internally but only
    /// as far as the caller's own budget allows. Give a realistic timeout rather
    /// than retrying tightly on an empty result.
    /// </para>
    /// </remarks>
    /// <param name="timeout">
    /// How long the pass may take, including any wait for a pass already running on this finder.
    /// When null, a finder with a natural end (<see cref="SerialDeviceFinder"/>,
    /// <see cref="HidDeviceFinder"/>) runs to that end, and a finder that only listens
    /// (<see cref="WifiDeviceFinder"/>, <see cref="MDnsDeviceFinder"/>) listens for 5 seconds.
    /// <see cref="Timeout.InfiniteTimeSpan"/> runs until <paramref name="cancellationToken"/> is
    /// cancelled, for a finder with no natural end.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The devices found.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or
    /// longer than a timer can run.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The finder has been disposed.</exception>
    Task<IReadOnlyList<IDeviceInfo>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
