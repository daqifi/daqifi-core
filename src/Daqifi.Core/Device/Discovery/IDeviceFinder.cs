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
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers devices asynchronously with a timeout.
    /// </summary>
    /// <param name="timeout">The timeout for discovery.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout);
}
