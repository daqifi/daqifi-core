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
                    var added = false;
                    try
                    {
                        // Bind to the discovery port (with ReuseAddress) rather than an ephemeral
                        // port so devices that target the well-known port for replies still reach
                        // us. Per-NIC sockets coexist on the same port via distinct (LocalAddress,
                        // port) tuples + SO_REUSEADDR.
                        udp = new UdpClient
                        {
                            EnableBroadcast = true,
                            ExclusiveAddressUse = false
                        };
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        try
                        {
                            udp.Client.Bind(new IPEndPoint(interfaceInfo.LocalAddress, _discoveryPort));
                        }
                        catch (SocketException)
                        {
                            // Fall back to an ephemeral port if the well-known discovery port
                            // can't be acquired on this platform/NIC (e.g. another process holds
                            // it). Devices that reply to source-port still reach us either way.
                            udp.Client.Bind(new IPEndPoint(interfaceInfo.LocalAddress, 0));
                        }
                        await udp.SendAsync(_queryCommandBytes, _queryCommandBytes.Length, interfaceInfo.BroadcastEndpoint);
                        perNicClients.Add((udp, interfaceInfo.LocalAddress));
                        added = true;
                    }
                    catch
                    {
                        // Skip this NIC; continue with others. Any setup or send failure is
                        // recoverable at the discovery level.
                    }
                    finally
                    {
                        if (!added)
                        {
                            udp?.Dispose();
                        }
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
            catch
            {
                // Catch-all so an unexpected exception type cannot fault this receive task
                // and hang DiscoverAsync via Task.WhenAll under the infinite-timeout overload.
                break;
            }

            // Defense-in-depth: any unexpected exception in payload processing or subscriber
            // dispatch must not fault this receive task. With parallel per-NIC loops awaited
            // via Task.WhenAll under the infinite-timeout overload, a single faulted task would
            // hang DiscoverAsync indefinitely.
            try
            {
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

                    // Invoke under the lock so DeviceDiscovered fires sequentially across the
                    // parallel per-NIC receive loops, matching the original (single-socket)
                    // sequential-callback contract that subscribers may depend on.
                    OnDeviceDiscovered(deviceInfo);
                }
            }
            catch
            {
                // Swallow malformed payloads and subscriber exceptions; keep receiving.
            }
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
    internal static DeviceType GetDeviceType(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
            return DeviceType.Unknown;

        return partNumber.ToLowerInvariant() switch
        {
            "nq1" => DeviceType.Nyquist1,
            "nq2" => DeviceType.Nyquist2,
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
            if (!ShouldIncludeInterface(
                    networkInterface.Name,
                    networkInterface.Description,
                    networkInterface.OperationalStatus,
                    networkInterface.NetworkInterfaceType,
                    networkInterface.Supports(NetworkInterfaceComponent.IPv4)))
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
    /// Returns true if a NIC matching the given metadata should be included in discovery.
    /// Centralizes the filter — Up + IPv4-capable + Ethernet/Wireless80211 + non-virtual —
    /// so a mixed-NIC list can be exercised in unit tests without instantiating real
    /// <see cref="NetworkInterface"/> objects. Internal for testing.
    /// </summary>
    internal static bool ShouldIncludeInterface(
        string? name,
        string? description,
        OperationalStatus operationalStatus,
        NetworkInterfaceType interfaceType,
        bool supportsIPv4)
    {
        if (operationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (!supportsIPv4)
        {
            return false;
        }

        if (interfaceType != NetworkInterfaceType.Ethernet &&
            interfaceType != NetworkInterfaceType.Wireless80211)
        {
            return false;
        }

        // Skip virtual/tunnel adapters (WSL2 mirrored vEthernet, Hyper-V, VirtualBox, VMware, TAP)
        // that frequently share a subnet with the real adapter and cause Windows routing to pick
        // the wrong egress NIC for broadcasts. See issue #179.
        if (IsVirtualOrTunnelInterface(name, description))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if the adapter looks like a virtual/tunnel interface that should be skipped
    /// (WSL2 mirrored vEthernet, Hyper-V, VirtualBox, VMware, TAP). Internal for testing.
    /// </summary>
    internal static bool IsVirtualOrTunnelInterface(string? name, string? description)
    {
        var n = (name ?? string.Empty).Trim();
        var d = (description ?? string.Empty).Trim();

        static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // Match keywords against BOTH name and description — virtualization markers can show
        // up in either field depending on platform/locale. Use specific TAP prefixes to avoid
        // false positives on legitimate physical NICs whose description happens to contain
        // those three letters.
        return Contains(n, "vEthernet") || Contains(d, "vEthernet") ||
               Contains(n, "Hyper-V") || Contains(d, "Hyper-V") ||
               Contains(n, "WSL") || Contains(d, "WSL") ||
               Contains(n, "VirtualBox") || Contains(d, "VirtualBox") ||
               Contains(n, "VMware") || Contains(d, "VMware") ||
               Contains(n, "TAP-Windows") || Contains(d, "TAP-Windows") ||
               Contains(n, "TAP-") || Contains(d, "TAP-") ||
               Contains(n, "TAP ") || Contains(d, "TAP ");
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
