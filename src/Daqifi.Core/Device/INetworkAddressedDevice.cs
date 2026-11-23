using System.Net;

namespace Daqifi.Core.Device;

/// <summary>
/// A device that is reached at a network address. Every device <see cref="DaqifiDeviceFactory"/>
/// connects over TCP (including WiFi devices from discovery) implements it; a device it connects
/// over serial does not.
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
    /// Gets the IP address of the device: for a factory-built device, the address the connection
    /// was made to (for a host-name connect, the address the name resolved to). <c>null</c> only
    /// when an implementation genuinely does not know it.
    /// </summary>
    IPAddress? IpAddress { get; }
}
