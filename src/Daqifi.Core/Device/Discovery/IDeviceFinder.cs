using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Interface for device discovery mechanisms.
/// </summary>
public interface IDeviceFinder
{
    /// <summary>
    /// Occurs when a device is discovered.
    /// </summary>
    event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Occurs when device discovery completes.
    /// </summary>
    event EventHandler? DiscoveryCompleted;

    /// <summary>
    /// Discovers devices asynchronously with a cancellation token.
    /// </summary>
    /// <remarks>
    /// An empty result means "nothing answered within the budget you gave", not
    /// "no device is attached". A transport may need to finish releasing resources
    /// from a previous pass that ended by timeout or cancellation — see
    /// <see cref="SerialDeviceFinder"/>, which absorbs that wait internally but only
    /// as far as the caller's own budget allows. Give a realistic timeout rather
    /// than retrying tightly on an empty result.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers devices asynchronously with a timeout.
    /// </summary>
    /// <remarks>
    /// Returns an empty result rather than throwing when the timeout elapses. See
    /// <see cref="DiscoverAsync(CancellationToken)"/> for why an empty result is not
    /// proof that no device is attached.
    /// </remarks>
    /// <param name="timeout">The timeout for discovery.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout);
}
