using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices with mDNS / DNS-SD (RFC 6762 / 6763) by browsing for the
/// <c>_daqifi._tcp.local.</c> service on 224.0.0.251:5353.
/// </summary>
/// <remarks>
/// <para>
/// This is a sibling of <see cref="WiFiDeviceFinder"/>, not a replacement: it produces the
/// same <see cref="IDeviceInfo"/> shape with <see cref="ConnectionType.WiFi"/>, so anything
/// that can connect to a broadcast-discovered device can connect to an mDNS-discovered one.
/// Run both — passing the two finders to
/// <see cref="AllTransportsDeviceFinder(System.Collections.Generic.IEnumerable{IDeviceFinder}, System.Func{IDeviceInfo, string})"/>
/// merges and deduplicates them — so devices on firmware without an mDNS responder are still
/// found over UDP broadcast.
/// </para>
/// <para>
/// The reason mDNS exists here at all is that subnet-directed UDP broadcast is unreliable on
/// an ordinary multi-AP home network: broadcast frames crossing an AP boundary on a lossy
/// 2.4 GHz radio are dropped often enough that ARP itself fails to resolve, so the broadcast
/// sweep silently returns nothing while the device is online the whole time. Multicast to the
/// mDNS group is the traffic every prosumer router already reflects across APs, SSIDs and
/// VLANs. See issue #183.
/// </para>
/// <para>
/// A pass is a one-shot browse, matching <see cref="WiFiDeviceFinder"/>'s semantics: the query
/// is (re)sent on a short retransmission schedule and every response received before the
/// timeout or cancellation is reported. Wrap it in <see cref="ContinuousDeviceFinder"/> for
/// ongoing discovery.
/// </para>
/// </remarks>
public sealed class MDnsDeviceFinder : DeviceFinderBase
{
    #region Constants

    /// <summary>
    /// The DNS-SD service type DAQiFi devices advertise. The <c>local.</c> domain is implicit.
    /// </summary>
    public const string DefaultServiceType = "_daqifi._tcp";

    /// <summary>
    /// TXT keys the device publishes (firmware daqifi-nyquist-firmware#345).
    /// </summary>
    private const string TxtKeySerialNumber = "sn";
    private const string TxtKeyPartNumber = "pn";
    private const string TxtKeyFirmwareVersion = "fw";
    private const string TxtKeyFriendlyName = "friendly";

    #endregion

    #region Private Types

    /// <summary>
    /// One local IPv4 interface address plus its mask, used both to send the query out of
    /// every eligible NIC and to attribute a reply to the NIC that can reach it.
    /// </summary>
    internal readonly struct LocalInterface
    {
        /// <summary>The local IPv4 address.</summary>
        public IPAddress Address { get; init; }

        /// <summary>The IPv4 subnet mask for <see cref="Address"/>.</summary>
        public IPAddress Mask { get; init; }
    }

    #endregion

    #region Private Fields

    private static readonly IPAddress MulticastGroup = new([224, 0, 0, 251]);

    // RFC 6762 §5.2 asks a one-shot browse to repeat its query with an increasing interval so a
    // single dropped multicast frame does not cost the whole pass -- which is the exact failure
    // this finder exists to survive. Offsets are from the start of the pass.
    private static readonly TimeSpan[] QuerySchedule =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    private readonly IReadOnlyList<string> _serviceLabels;
    private readonly byte[] _queryBytes;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance browsing for the default <c>_daqifi._tcp</c> service.
    /// </summary>
    public MDnsDeviceFinder() : this(DefaultServiceType)
    {
    }

    /// <summary>
    /// Initializes a new instance browsing for a specific DNS-SD service type.
    /// </summary>
    /// <param name="serviceType">
    /// The service type, e.g. <c>_daqifi._tcp</c>. The <c>local.</c> domain is appended when absent.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when the service type is empty or malformed.</exception>
    public MDnsDeviceFinder(string serviceType)
    {
        _serviceLabels = MDnsMessage.ParseServiceLabels(serviceType);
        _queryBytes = MDnsMessage.BuildPtrQuery(_serviceLabels);
        ServiceType = string.Join(".", _serviceLabels);
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the fully qualified service type being browsed, e.g. <c>_daqifi._tcp.local</c>.
    /// </summary>
    public string ServiceType { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Browses for devices asynchronously until the token is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to end the browse.</param>
    /// <returns>A task containing the devices that answered.</returns>
    /// <remarks>
    /// Like <see cref="WiFiDeviceFinder"/>, this overload runs until cancelled — a browse has
    /// no natural end. Prefer <see cref="DeviceFinderBase.DiscoverAsync(TimeSpan)"/>, or pass a
    /// token that will actually fire.
    /// </remarks>
    public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await DiscoverySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discoveredDevices = new List<IDeviceInfo>();
            var interfaces = GetMulticastInterfaces();

            if (interfaces.Count == 0)
            {
                OnDiscoveryCompleted();
                return discoveredDevices;
            }

            // One socket bound to the wildcard address, not one per NIC as the broadcast finder
            // uses: responders answer to the multicast group, and on Linux/macOS a socket bound
            // to a unicast address never sees a datagram addressed to 224.0.0.251. The receiving
            // NIC is therefore recovered per reply (see ResolveLocalInterfaceAddress) instead of
            // being read off the socket.
            using var udp = TryCreateListener();
            if (udp is null)
            {
                OnDiscoveryCompleted();
                return discoveredDevices;
            }

            var joined = new List<LocalInterface>();
            foreach (var localInterface in interfaces)
            {
                try
                {
                    udp.JoinMulticastGroup(MulticastGroup, localInterface.Address);
                    joined.Add(localInterface);
                }
                catch (SocketException)
                {
                    // This NIC cannot join the group (no multicast route, adapter going down).
                    // Others still work, so skip it rather than failing the pass.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }

            if (joined.Count == 0)
            {
                OnDiscoveryCompleted();
                return discoveredDevices;
            }

            await Task.WhenAll(
                    ReceiveLoopAsync(udp, interfaces, discoveredDevices, cancellationToken),
                    SendQueriesAsync(udp, joined, cancellationToken))
                .ConfigureAwait(false);

            OnDiscoveryCompleted();
            return discoveredDevices;
        }
        finally
        {
            DiscoverySemaphore.Release();
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Creates the listening socket bound to the mDNS port, or null if the port cannot be shared.
    /// </summary>
    private static UdpClient? TryCreateListener()
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            TryEnableReusePort(udp.Client);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MDnsMessage.MulticastPort));

            try
            {
                // RFC 6762 §11: mDNS packets are sent with IP TTL 255.
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            }
            catch (SocketException)
            {
                // Not fatal — the default TTL still reaches the local link.
            }

            return udp;
        }
        catch (SocketException)
        {
            // Port 5353 is held exclusively by another process on this host. Nothing to do but
            // report an empty pass; the broadcast finder is the fallback.
            udp?.Dispose();
            return null;
        }
        catch (ObjectDisposedException)
        {
            udp?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Best-effort SO_REUSEPORT, which BSD-derived stacks require (in addition to SO_REUSEADDR)
    /// before a second socket may bind a port an existing socket already holds. Without it,
    /// binding 5353 fails on any macOS or Linux host running its own mDNS daemon — i.e. nearly
    /// all of them. Multicast datagrams are delivered to every socket joined to the group, so
    /// sharing the port does not take traffic away from that daemon.
    /// </summary>
    private static void TryEnableReusePort(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // SO_REUSEADDR alone already permits a shared bind on Windows.
        }

        // SOL_SOCKET / SO_REUSEPORT differ between Linux and the BSD family (which includes macOS).
        var (level, name) = OperatingSystem.IsLinux() ? (1, 15) : (0xFFFF, 0x0200);

        try
        {
            socket.SetRawSocketOption(level, name, BitConverter.GetBytes(1));
        }
        catch (SocketException)
        {
            // Unsupported on this platform; the plain SO_REUSEADDR bind may still succeed.
        }
        catch (PlatformNotSupportedException)
        {
            // Ditto.
        }
    }

    /// <summary>
    /// Sends the browse query out of every joined NIC on the retransmission schedule.
    /// </summary>
    private async Task SendQueriesAsync(UdpClient udp, IReadOnlyList<LocalInterface> interfaces, CancellationToken cancellationToken)
    {
        var target = new IPEndPoint(MulticastGroup, MDnsMessage.MulticastPort);

        foreach (var delay in QuerySchedule)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var localInterface in interfaces)
            {
                try
                {
                    // Pick the egress NIC explicitly: the default multicast route sends out one
                    // adapter only, which on a multi-homed host is regularly the wrong one.
                    udp.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        localInterface.Address.GetAddressBytes());

                    await udp.SendAsync(_queryBytes, _queryBytes.Length, target).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // This NIC failed to send; the others still carry the query.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Receives responses until cancellation, mirroring <see cref="WiFiDeviceFinder"/>'s loop:
    /// no exception from a datagram or a subscriber may fault the task, because it is awaited
    /// with <see cref="Task.WhenAll(Task[])"/> and a faulted task would hang the pass.
    /// </summary>
    private async Task ReceiveLoopAsync(
        UdpClient udp,
        IReadOnlyList<LocalInterface> interfaces,
        List<IDeviceInfo> discoveredDevices,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
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
                break;
            }

            try
            {
                HandleDatagram(result.Buffer, result.Buffer.Length, result.RemoteEndPoint, interfaces, discoveredDevices);
            }
            catch
            {
                // Swallow malformed payloads and subscriber exceptions; keep receiving.
            }
        }
    }

    /// <summary>
    /// Parses one received datagram and publishes any new devices it describes. Internal so the
    /// whole decode-to-<see cref="IDeviceInfo"/> path — including deduplication and event
    /// dispatch — can be exercised from a recorded packet without a live network.
    /// </summary>
    /// <param name="buffer">The datagram.</param>
    /// <param name="length">Valid byte count in <paramref name="buffer"/>.</param>
    /// <param name="remoteEndPoint">The sender, used as the address fallback when no A record arrives.</param>
    /// <param name="interfaces">The local interfaces, used to attribute the reply to a NIC.</param>
    /// <param name="discoveredDevices">Accumulator for this pass; also the deduplication set.</param>
    internal void HandleDatagram(
        byte[] buffer,
        int length,
        IPEndPoint remoteEndPoint,
        IReadOnlyList<LocalInterface> interfaces,
        List<IDeviceInfo> discoveredDevices)
    {
        if (!MDnsMessage.TryParseResponse(buffer, length, out var records))
        {
            return;
        }

        foreach (var device in MapDevices(records, _serviceLabels, remoteEndPoint.Address, interfaces))
        {
            lock (discoveredDevices)
            {
                if (discoveredDevices.Any(existing => IsDuplicateDevice(existing, device)))
                {
                    continue;
                }

                discoveredDevices.Add(device);

                // Raise under the lock so subscribers see one device at a time, matching the
                // broadcast finder's sequential-callback contract.
                OnDeviceDiscovered(device);
            }
        }
    }

    /// <summary>
    /// Turns a parsed response into device entries. Internal and pure so the record-to-device
    /// mapping is unit-testable from a synthesized transcript.
    /// </summary>
    /// <param name="records">The records from one response.</param>
    /// <param name="serviceLabels">The service type being browsed.</param>
    /// <param name="senderAddress">The responder's address, used when no A record is present.</param>
    /// <param name="interfaces">The local interfaces, for <see cref="IDeviceInfo.LocalInterfaceAddress"/>.</param>
    /// <returns>One entry per resolvable service instance.</returns>
    internal static IReadOnlyList<DeviceInfo> MapDevices(
        IReadOnlyList<MDnsResourceRecord> records,
        IReadOnlyList<string> serviceLabels,
        IPAddress senderAddress,
        IReadOnlyList<LocalInterface> interfaces)
    {
        var devices = new List<DeviceInfo>();

        // A TTL of zero is a goodbye (RFC 6762 §10.1): the device is announcing that it is
        // leaving, so its instance must not be reported even though the same packet still
        // carries that instance's SRV/TXT/A data.
        var departed = records
            .Where(r => r.RecordType == MDnsMessage.TypePtr && r.Ttl == 0 &&
                        MDnsMessage.NameEquals(r.Name, serviceLabels) && r.Target is not null)
            .Select(r => r.Target!)
            .ToList();

        var instances = new List<IReadOnlyList<string>>();

        foreach (var candidate in EnumerateInstanceNames(records, serviceLabels))
        {
            if (departed.Any(gone => MDnsMessage.NameEquals(gone, candidate)) ||
                instances.Any(seen => MDnsMessage.NameEquals(seen, candidate)))
            {
                continue;
            }

            instances.Add(candidate);
        }

        foreach (var instance in instances)
        {
            var srv = records.FirstOrDefault(r =>
                r.RecordType == MDnsMessage.TypeSrv && r.Ttl > 0 && MDnsMessage.NameEquals(r.Name, instance));

            if (srv?.Target is null || srv.Port == 0)
            {
                // Browse-only reply (PTR with no SRV yet), or an unusable port. Nothing to
                // connect to, so do not manufacture a device entry.
                continue;
            }

            var txt = records.FirstOrDefault(r =>
                r.RecordType == MDnsMessage.TypeTxt && r.Ttl > 0 && MDnsMessage.NameEquals(r.Name, instance));

            var attributes = ParseTxtAttributes(txt?.TxtStrings);

            // Prefer the advertised A record; fall back to the responder's own source address,
            // which is the device itself, so a reply that lost its A record still yields a
            // connectable entry rather than being dropped.
            var address = records.FirstOrDefault(r =>
                              r.RecordType == MDnsMessage.TypeA && r.Ttl > 0 &&
                              r.Address is not null && MDnsMessage.NameEquals(r.Name, srv.Target))
                          ?.Address
                          ?? senderAddress;

            attributes.TryGetValue(TxtKeyFriendlyName, out var friendlyName);
            attributes.TryGetValue(TxtKeySerialNumber, out var serialNumber);
            attributes.TryGetValue(TxtKeyFirmwareVersion, out var firmwareVersion);
            attributes.TryGetValue(TxtKeyPartNumber, out var partNumber);

            devices.Add(new DeviceInfo
            {
                Name = string.IsNullOrWhiteSpace(friendlyName) ? instance[0] : friendlyName!,
                SerialNumber = serialNumber ?? string.Empty,
                FirmwareVersion = firmwareVersion ?? string.Empty,
                IPAddress = address,
                // The advertisement carries no MAC address, and the low two bytes embedded in the
                // hostname are not one. Leave it null rather than inventing a partial value that
                // dedupe elsewhere would compare against a real MAC.
                MacAddress = null,
                Port = srv.Port,
                LocalInterfaceAddress = ResolveLocalInterfaceAddress(address, interfaces),
                Type = DiscoveryDeviceTypeMapper.FromPartNumber(partNumber),
                IsPowerOn = true,
                ConnectionType = ConnectionType.WiFi
            });
        }

        return devices;
    }

    /// <summary>
    /// Yields every service-instance name the response refers to, from PTR answers and from
    /// SRV records that name an instance of the browsed service type directly (some responders
    /// answer a resolve without repeating the PTR).
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> EnumerateInstanceNames(
        IReadOnlyList<MDnsResourceRecord> records,
        IReadOnlyList<string> serviceLabels)
    {
        foreach (var record in records)
        {
            if (record.RecordType == MDnsMessage.TypePtr &&
                record.Ttl > 0 &&
                MDnsMessage.NameEquals(record.Name, serviceLabels) &&
                MDnsMessage.IsInstanceOf(record.Target, serviceLabels))
            {
                yield return record.Target!;
            }
            else if (record.RecordType == MDnsMessage.TypeSrv &&
                     record.Ttl > 0 &&
                     MDnsMessage.IsInstanceOf(record.Name, serviceLabels))
            {
                yield return record.Name;
            }
        }
    }

    /// <summary>
    /// Splits DNS-SD TXT strings into key/value attributes. Keys are compared case-insensitively
    /// and the first occurrence of a key wins (RFC 6763 §6.4); a string with no '=' is a boolean
    /// attribute and carries no value we need.
    /// </summary>
    private static Dictionary<string, string> ParseTxtAttributes(IReadOnlyList<string>? txtStrings)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (txtStrings is null)
        {
            return attributes;
        }

        foreach (var entry in txtStrings)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = entry.Substring(0, separator);
            if (!attributes.ContainsKey(key))
            {
                attributes[key] = entry.Substring(separator + 1);
            }
        }

        return attributes;
    }

    /// <summary>
    /// Returns the local address of the interface whose subnet contains the device, or null when
    /// no interface matches. Internal and pure for testing.
    /// </summary>
    /// <param name="deviceAddress">The discovered device's address.</param>
    /// <param name="interfaces">The candidate local interfaces.</param>
    /// <returns>The matching local address, or null.</returns>
    /// <remarks>
    /// The broadcast finder reads this off the socket it bound to a specific NIC. A shared mDNS
    /// listener has to be bound to the wildcard address to receive multicast at all, so the NIC
    /// is recovered by matching the device's address against each interface's subnet instead —
    /// which yields the same answer for the case that matters: which local address a TCP
    /// connection to this device should bind to on a multi-NIC host.
    /// </remarks>
    internal static IPAddress? ResolveLocalInterfaceAddress(IPAddress deviceAddress, IReadOnlyList<LocalInterface> interfaces)
    {
        if (deviceAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var deviceBytes = deviceAddress.GetAddressBytes();

        foreach (var localInterface in interfaces)
        {
            var localBytes = localInterface.Address.GetAddressBytes();
            var maskBytes = localInterface.Mask.GetAddressBytes();

            if (localBytes.Length != 4 || maskBytes.Length != 4)
            {
                continue;
            }

            var sameSubnet = true;
            for (var i = 0; i < 4; i++)
            {
                if ((localBytes[i] & maskBytes[i]) != (deviceBytes[i] & maskBytes[i]))
                {
                    sameSubnet = false;
                    break;
                }
            }

            if (sameSubnet)
            {
                return localInterface.Address;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether two entries describe the same device, by serial number when both carry one
    /// and otherwise by endpoint.
    /// </summary>
    private static bool IsDuplicateDevice(IDeviceInfo existing, IDeviceInfo candidate)
    {
        if (!string.IsNullOrEmpty(existing.SerialNumber) && !string.IsNullOrEmpty(candidate.SerialNumber))
        {
            return existing.SerialNumber.Equals(candidate.SerialNumber, StringComparison.OrdinalIgnoreCase);
        }

        return Equals(existing.IPAddress, candidate.IPAddress) && existing.Port == candidate.Port;
    }

    /// <summary>
    /// Enumerates the local IPv4 interfaces eligible for a multicast browse. Reuses
    /// <see cref="WiFiDeviceFinder.ShouldIncludeInterface"/> so the two network finders cannot
    /// drift apart on which adapters are real (the virtual/tunnel adapter problem of issue #179),
    /// and additionally requires multicast support.
    /// </summary>
    private static List<LocalInterface> GetMulticastInterfaces()
    {
        var interfaces = new List<LocalInterface>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!networkInterface.SupportsMulticast)
            {
                continue;
            }

            if (!WiFiDeviceFinder.ShouldIncludeInterface(
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

                interfaces.Add(new LocalInterface
                {
                    Address = unicastIpAddressInformation.Address,
                    Mask = unicastIpAddressInformation.IPv4Mask
                });
            }
        }

        return interfaces;
    }

    #endregion
}
