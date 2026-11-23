using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Daqifi.Core.Device;

/// <summary>
/// The streaming device <see cref="DaqifiDeviceFactory"/> returns for a TCP connection. It is a
/// <see cref="DaqifiStreamingDevice"/> in every respect, and additionally implements
/// <see cref="INetworkAddressedDevice"/>, reporting the address the connection reached.
/// </summary>
/// <remarks>
/// Internal on purpose: a caller holding the device asks <c>device is INetworkAddressedDevice</c>
/// and never needs to name this type. Keeping the facet off <see cref="DaqifiDevice"/> itself is
/// what lets a serial device answer "no" (issue #616).
/// <see cref="INetworkAddressedDevice.IpAddress"/> is implemented by the inherited
/// <see cref="DaqifiDevice.IpAddress"/>.
/// </remarks>
internal sealed class NetworkStreamingDevice : DaqifiStreamingDevice, INetworkAddressedDevice
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkStreamingDevice"/> class.
    /// </summary>
    /// <param name="name">The name of the device.</param>
    /// <param name="transport">The connected TCP transport; the device takes ownership of it.</param>
    /// <param name="ipAddress">The address the transport reached.</param>
    /// <param name="logger">Optional logger for device diagnostics.</param>
    public NetworkStreamingDevice(string name, TcpStreamTransport transport, IPAddress? ipAddress, ILogger? logger)
        : base(name, transport, ipAddress, logger)
    {
    }
}
