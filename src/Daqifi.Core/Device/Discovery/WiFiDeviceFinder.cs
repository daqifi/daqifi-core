using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Network;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices on the network using UDP broadcast on port 30303.
/// </summary>
public class WiFiDeviceFinder : IDeviceFinder, IDisposable
{
    #region Constants

    private const string DaqifiFinderQuery = "DAQiFi?\r\n";
    private const string NativeFinderQuery = "Discovery: Who is out there?\r\n";
    private const string PowerEvent = "Power event occurred";
    private const int DefaultDiscoveryPort = 30303;
    private const int DefaultTimeoutSeconds = 5;

    #endregion

    #region Private Types

    /// <summary>
    /// Represents network interface information for discovery broadcasts.
    /// </summary>
    private readonly struct NetworkInterfaceInfo
    {
        /// <summary>
        /// The broadcast endpoint to send discovery queries to.
        /// </summary>
        public IPEndPoint BroadcastEndpoint { get; init; }

        /// <summary>
        /// The local interface address.
        /// </summary>
        public IPAddress LocalAddress { get; init; }
    }

    #endregion

    #region Private Fields

    private readonly int _discoveryPort;
    private readonly byte[] _queryCommandBytes;
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private bool _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a device is discovered.
    /// </summary>
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Occurs when device discovery completes.
    /// </summary>
    public event EventHandler? DiscoveryCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the WiFiDeviceFinder class with default port 30303.
    /// </summary>
    public WiFiDeviceFinder() : this(DefaultDiscoveryPort)
    {
    }

    /// <summary>
    /// Initializes a new instance of the WiFiDeviceFinder class.
    /// </summary>
    /// <param name="discoveryPort">The UDP port to use for discovery.</param>
    public WiFiDeviceFinder(int discoveryPort)
    {
        _discoveryPort = discoveryPort;
        _queryCommandBytes = Encoding.ASCII.GetBytes(DaqifiFinderQuery);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Discovers devices asynchronously with a cancellation token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        return await DiscoverAsync(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>
    /// Discovers devices asynchronously with a timeout.
    /// </summary>
    /// <param name="timeout">The timeout for discovery.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
    {
        return await DiscoverAsync(timeout, CancellationToken.None);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Internal implementation of discovery with timeout and cancellation.
    /// </summary>
    private async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Prevent concurrent discovery operations
        await _discoverySemaphore.WaitAsync(cancellationToken);
        try
        {
            var discoveredDevices = new List<IDeviceInfo>();
            var networkInterfaces = GetAllNetworkInterfaces(_discoveryPort);

            if (networkInterfaces.Count == 0)
            {
                OnDiscoveryCompleted();
                return discoveredDevices;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            // Bind a separate UdpClient per NIC so the OS routes each broadcast out the intended adapter
            // and replies arrive on the socket whose bound IP is the actual local interface — avoiding
            // misrouting on hosts where virtual NICs (WSL2 mirrored, Hyper-V) share a subnet with the real one.
            var perNicClients = new List<(UdpClient Client, IPAddress LocalAddress)>();
            try
            {
                foreach (var interfaceInfo in networkInterfaces)
                {
                    UdpClient? udp = null;
                    try
                    {
                        udp = new UdpClient(new IPEndPoint(interfaceInfo.LocalAddress, 0))
                        {
                            EnableBroadcast = true
                        };
                        await udp.SendAsync(_queryCommandBytes, _queryCommandBytes.Length, interfaceInfo.BroadcastEndpoint);
                        perNicClients.Add((udp, interfaceInfo.LocalAddress));
                    }
                    catch (SocketException)
                    {
                        udp?.Dispose();
                    }
                }

                if (perNicClients.Count == 0)
                {
                    OnDiscoveryCompleted();
                    return discoveredDevices;
                }

                var receiveTasks = perNicClients
                    .Select(c => ReceiveLoopAsync(c.Client, c.LocalAddress, discoveredDevices, cts.Token))
                    .ToArray();

                await Task.WhenAll(receiveTasks);
            }
            finally
            {
                foreach (var (client, _) in perNicClients)
                {
                    try { client.Dispose(); } catch { /* ignore */ }
                }
            }

            OnDiscoveryCompleted();
            return discoveredDevices;
        }
        finally
        {
            _discoverySemaphore.Release();
        }
    }

    /// <summary>
    /// Receives discovery responses on a single NIC-bound socket until cancellation.
    /// The socket's bound IP is the authoritative LocalInterfaceAddress for any reply it receives.
    /// </summary>
    private async Task ReceiveLoopAsync(UdpClient udp, IPAddress localAddress, List<IDeviceInfo> discoveredDevices, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var receivedText = Encoding.ASCII.GetString(result.Buffer);
            if (!IsValidDiscoveryMessage(receivedText))
            {
                continue;
            }

            var deviceInfo = ParseDeviceInfo(result.Buffer, result.RemoteEndPoint, localAddress);
            if (deviceInfo == null)
            {
                continue;
            }

            lock (discoveredDevices)
            {
                if (discoveredDevices.Any(d => IsDuplicateDevice(d, deviceInfo)))
                {
                    continue;
                }
                discoveredDevices.Add(deviceInfo);
            }

            OnDeviceDiscovered(deviceInfo);
        }
    }

    /// <summary>
    /// Determines if a received message is a valid discovery response.
    /// </summary>
    private static bool IsValidDiscoveryMessage(string receivedText)
    {
        return !receivedText.Contains(NativeFinderQuery) &&
               !receivedText.Contains(DaqifiFinderQuery) &&
               !receivedText.Contains(PowerEvent);
    }

    /// <summary>
    /// Parses device information from protobuf message.
    /// </summary>
    /// <param name="data">The raw protobuf data.</param>
    /// <param name="remoteEndPoint">The remote endpoint that sent the response.</param>
    /// <param name="localInterfaceAddress">The local interface address that discovered this device.</param>
    private static IDeviceInfo? ParseDeviceInfo(byte[] data, IPEndPoint remoteEndPoint, IPAddress? localInterfaceAddress)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var message = DaqifiOutMessage.Parser.ParseDelimitedFrom(stream);

            var deviceInfo = new DeviceInfo
            {
                Name = message.HostName ?? "Unknown",
                SerialNumber = message.DeviceSn.ToString(CultureInfo.InvariantCulture),
                FirmwareVersion = message.DeviceFwRev ?? string.Empty,
                IPAddress = remoteEndPoint.Address,
                MacAddress = NetworkAddressHelper.GetMacAddressString(message),
                Port = (int)message.DevicePort,
                LocalInterfaceAddress = localInterfaceAddress,
                Type = GetDeviceType(message.DevicePn),
                IsPowerOn = message.PwrStatus == 1,
                ConnectionType = ConnectionType.WiFi
            };

            return deviceInfo;
        }
        catch (Exception)
        {
            // Invalid protobuf message, return null
            return null;
        }
    }

    /// <summary>
    /// Determines device type from part number.
    /// </summary>
    private static DeviceType GetDeviceType(string partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
            return DeviceType.Unknown;

        return partNumber.ToLowerInvariant() switch
        {
            "nq1" => DeviceType.Nyquist1,
            "nq3" => DeviceType.Nyquist3,
            _ => DeviceType.Unknown
        };
    }

    /// <summary>
    /// Checks if two device info objects represent the same device.
    /// </summary>
    private static bool IsDuplicateDevice(IDeviceInfo existing, IDeviceInfo newDevice)
    {
        // Compare by MAC address if available, otherwise by serial number
        if (!string.IsNullOrEmpty(existing.MacAddress) && !string.IsNullOrEmpty(newDevice.MacAddress))
        {
            return existing.MacAddress.Equals(newDevice.MacAddress, StringComparison.OrdinalIgnoreCase);
        }

        return existing.SerialNumber.Equals(newDevice.SerialNumber, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all network interface information for active interfaces.
    /// </summary>
    private static List<NetworkInterfaceInfo> GetAllNetworkInterfaces(int port)
    {
        var interfaces = new List<NetworkInterfaceInfo>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                !networkInterface.Supports(NetworkInterfaceComponent.IPv4) ||
                (networkInterface.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                 networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211))
            {
                continue;
            }

            // Skip virtual/tunnel adapters (WSL2 mirrored vEthernet, Hyper-V, VirtualBox, VMware, TAP)
            // that frequently share a subnet with the real adapter and cause Windows routing to pick
            // the wrong egress NIC for broadcasts. See issue #179.
            if (IsVirtualOrTunnelInterface(networkInterface.Name, networkInterface.Description))
            {
                continue;
            }

            var ipProperties = networkInterface.GetIPProperties();
            if (ipProperties == null)
            {
                continue;
            }

            foreach (var unicastIpAddressInformation in ipProperties.UnicastAddresses)
            {
                if (unicastIpAddressInformation.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicastIpAddressInformation.IPv4Mask == null ||
                    unicastIpAddressInformation.IPv4Mask.Equals(IPAddress.Any))
                {
                    continue;
                }

                var ipAddress = unicastIpAddressInformation.Address;
                var subnetMask = unicastIpAddressInformation.IPv4Mask;

                var ipBytes = ipAddress.GetAddressBytes();
                var maskBytes = subnetMask.GetAddressBytes();
                if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                var broadcastBytes = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
                }

                var broadcastAddress = new IPAddress(broadcastBytes);
                var endpoint = new IPEndPoint(broadcastAddress, port);

                interfaces.Add(new NetworkInterfaceInfo
                {
                    BroadcastEndpoint = endpoint,
                    LocalAddress = ipAddress
                });
            }
        }

        return interfaces;
    }

    /// <summary>
    /// Returns true if the adapter looks like a virtual/tunnel interface that should be skipped
    /// (WSL2 mirrored vEthernet, Hyper-V, VirtualBox, VMware, TAP). Internal for testing.
    /// </summary>
    internal static bool IsVirtualOrTunnelInterface(string? name, string? description)
    {
        var n = name ?? string.Empty;
        var d = description ?? string.Empty;

        return n.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ||
               d.IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
               d.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
               d.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
               d.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0 ||
               d.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
               d.IndexOf("TAP", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Raises the DeviceDiscovered event.
    /// </summary>
    protected virtual void OnDeviceDiscovered(IDeviceInfo deviceInfo)
    {
        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(deviceInfo));
    }

    /// <summary>
    /// Raises the DiscoveryCompleted event.
    /// </summary>
    protected virtual void OnDiscoveryCompleted()
    {
        DiscoveryCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WiFiDeviceFinder));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the device finder.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _discoverySemaphore.Dispose();
            _disposed = true;
        }
    }

    #endregion
}
