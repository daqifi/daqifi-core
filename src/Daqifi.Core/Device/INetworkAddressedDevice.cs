using System.Net;

namespace Daqifi.Core.Device;

/// <summary>
/// A device that can be reached at a network address. Implemented by <see cref="DaqifiDevice"/>.
/// </summary>
/// <remarks>
/// <para>
/// A capability interface rather than a member of <see cref="IDevice"/>, matching
/// <see cref="ILiveSampleSource"/> and <see cref="SdCard.ISdCardOperations"/>: a caller holding a
/// device asks <c>if (device is INetworkAddressedDevice net)</c>. An IP address is a WiFi/TCP
/// fact, and a device reached over USB CDC or USB HID never has one, so <see cref="IDevice"/> no
/// longer makes every implementation answer the question. Before 2.0 it did, and every
/// non-network device carried a stub that always returned <c>null</c> (issue #616).
/// </para>
/// </remarks>
public interface INetworkAddressedDevice
{
    /// <summary>
    /// Gets the IP address of the device, or <c>null</c> when it is not known.
    /// </summary>
    IPAddress? IpAddress { get; }
}
