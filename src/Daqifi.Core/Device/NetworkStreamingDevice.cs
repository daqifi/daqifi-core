using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Daqifi.Core.Device;

/// <summary>
/// The streaming device <see cref="DaqifiDeviceFactory"/> returns for a TCP connection. It is a
/// <see cref="DaqifiStreamingDevice"/> in every respect, and additionally implements
/// <see cref="INetworkAddressedDevice"/>, reporting the address its transport is connected to.
/// </summary>
/// <remarks>
/// <para>
/// Internal on purpose: a caller holding the device asks <c>device is INetworkAddressedDevice</c>
/// and never needs to name this type. Keeping the facet off <see cref="DaqifiDevice"/> itself is
/// what lets a serial device answer "no" (issue #616).
/// </para>
/// <para>
/// The address is read from the transport on every call rather than captured at construction:
/// a host-name transport re-resolves on reconnect, and the device must report the peer it is
/// actually talking to.
/// </para>
/// </remarks>
internal sealed class NetworkStreamingDevice : DaqifiStreamingDevice, INetworkAddressedDevice
{
    private readonly TcpStreamTransport _tcpTransport;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkStreamingDevice"/> class.
    /// </summary>
    /// <param name="name">The name of the device.</param>
    /// <param name="transport">The TCP transport; the device takes ownership of it.</param>
    /// <param name="logger">Optional logger for device diagnostics.</param>
    public NetworkStreamingDevice(string name, TcpStreamTransport transport, ILogger? logger)
        : base(name, transport, logger)
    {
        _tcpTransport = transport;
    }

    /// <inheritdoc />
    public IPAddress? IpAddress => _tcpTransport.RemoteAddress;
}
