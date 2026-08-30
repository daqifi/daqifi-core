using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Tests for the mDNS finder (issue #183). The decode path — datagram to
/// <see cref="IDeviceInfo"/>, deduplication and event dispatch included — is driven from
/// synthesized advertisements through the same internal entry point the receive loop calls,
/// so none of it needs a network, a device or an mDNS responder on the build machine.
/// </summary>
public class MDnsDeviceFinderTests
{
    private static readonly IReadOnlyList<string> DaqifiService = MDnsMessage.ParseServiceLabels("_daqifi._tcp");
    private static readonly IPAddress DeviceAddress = IPAddress.Parse("192.168.1.39");
    private static readonly IPEndPoint DeviceEndPoint = new(DeviceAddress, MDnsMessage.MulticastPort);

    private static readonly MDnsDeviceFinder.LocalInterface[] Interfaces =
    [
        new() { Address = IPAddress.Parse("10.0.0.5"), Mask = IPAddress.Parse("255.255.255.0") },
        new() { Address = IPAddress.Parse("192.168.1.50"), Mask = IPAddress.Parse("255.255.255.0") }
    ];

    #region Advertisement to device mapping

    [Fact]
    public void MapDevices_MapsTheFirmwareAdvertisementOntoDeviceInfo()
    {
        var device = Assert.Single(MapAdvertisement(MDnsResponseBuilder.DeviceAdvertisement()));

        Assert.Equal("Bench Nyquist", device.Name);                  // TXT friendly=
        Assert.Equal("9090539562006014104", device.SerialNumber);    // TXT sn=
        Assert.Equal("3.7.3", device.FirmwareVersion);               // TXT fw=
        Assert.Equal(DeviceType.Nyquist1, device.Type);              // TXT pn=Nq1
        Assert.Equal(DeviceAddress, device.IPAddress);               // A record
        Assert.Equal(9760, device.Port);                             // SRV port
        Assert.Equal(ConnectionType.WiFi, device.ConnectionType);
        Assert.True(device.IsPowerOn);

        // The advertisement carries no MAC; inventing one from the hostname's two MAC bytes
        // would collide with the real MACs the broadcast finder reports.
        Assert.Null(device.MacAddress);

        // Attributed to the NIC that can actually reach 192.168.1.39.
        Assert.Equal(IPAddress.Parse("192.168.1.50"), device.LocalInterfaceAddress);
    }

    [Fact]
    public void MapDevices_FallsBackToTheInstanceNameWhenNoFriendlyName()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement(
            txtStrings: ["sn=1234", "pn=Nq2", "fw=3.7.3", "hw=2.0.0"]);

        var device = Assert.Single(MapAdvertisement(packet));

        Assert.Equal("DAQiFi-95A7", device.Name);
        Assert.Equal(DeviceType.Nyquist2, device.Type);
    }

    [Fact]
    public void MapDevices_HandlesAConflictResolvedInstanceName()
    {
        // RFC 6762 §9: a second device that probes into a name collision renames itself.
        var packet = MDnsResponseBuilder.DeviceAdvertisement(
            instanceLabel: "DAQiFi-95A7-2",
            host: "daqifi-95a7-2.local",
            deviceIp: "192.168.1.41",
            txtStrings: ["sn=4321", "pn=Nq1"]);

        var device = Assert.Single(MapAdvertisement(packet));

        Assert.Equal("DAQiFi-95A7-2", device.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.41"), device.IPAddress);
        Assert.Equal("4321", device.SerialNumber);
    }

    [Fact]
    public void MapDevices_IgnoresAGoodbyePacket()
    {
        // TTL 0 on the PTR means the device is leaving (RFC 6762 §10.1), even though the same
        // packet still carries its SRV/TXT/A data.
        var packet = MDnsResponseBuilder.DeviceAdvertisement(ptrTtl: 0);

        Assert.Empty(MapAdvertisement(packet));
    }

    [Fact]
    public void MapDevices_FallsBackToTheSenderAddressWhenTheARecordIsMissing()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement(deviceIp: null);

        var device = Assert.Single(MapAdvertisement(packet));

        Assert.Equal(DeviceAddress, device.IPAddress);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), device.LocalInterfaceAddress);
    }

    [Fact]
    public void MapDevices_SkipsAnInstanceWithNoServiceRecord()
    {
        // A browse-only answer: the service exists but nothing says where to connect.
        var packet = new MDnsResponseBuilder()
            .AddPtr(MDnsResponseBuilder.ServiceType, "DAQiFi-95A7." + MDnsResponseBuilder.ServiceType)
            .Build();

        Assert.Empty(MapAdvertisement(packet));
    }

    [Fact]
    public void MapDevices_SkipsAnInstanceAdvertisingPortZero()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement(port: 0);

        Assert.Empty(MapAdvertisement(packet));
    }

    [Fact]
    public void MapDevices_IgnoresOtherServicesOnTheNetwork()
    {
        // A shared 5353 socket sees every responder on the link, not just ours.
        var packet = new MDnsResponseBuilder()
            .AddPtr("_airplay._tcp.local", "Living Room._airplay._tcp.local")
            .AddSrv("Living Room._airplay._tcp.local", "appletv.local", 7000)
            .AddA("appletv.local", IPAddress.Parse("192.168.1.77"))
            .Build();

        Assert.Empty(MapAdvertisement(packet));
    }

    [Fact]
    public void MapDevices_ResolvesAnInstanceAdvertisedWithoutAPointerRecord()
    {
        // Some responders answer a resolve with SRV/TXT/A and no PTR.
        var instance = "DAQiFi-95A7." + MDnsResponseBuilder.ServiceType;
        var packet = new MDnsResponseBuilder()
            .AddSrv(instance, "daqifi-95a7.local", 9760)
            .AddTxt(instance, ["sn=1", "pn=Nq3"])
            .AddA("daqifi-95a7.local", DeviceAddress)
            .Build();

        var device = Assert.Single(MapAdvertisement(packet));

        Assert.Equal(DeviceType.Nyquist3, device.Type);
        Assert.Equal(9760, device.Port);
    }

    [Fact]
    public void MapDevices_ReportsTwoDistinctDevicesFromOneDatagram()
    {
        var first = "DAQiFi-95A7." + MDnsResponseBuilder.ServiceType;
        var second = "DAQiFi-A4B1." + MDnsResponseBuilder.ServiceType;
        var packet = new MDnsResponseBuilder()
            .AddPtr(MDnsResponseBuilder.ServiceType, first)
            .AddPtr(MDnsResponseBuilder.ServiceType, second)
            .AddSrv(first, "daqifi-95a7.local", 9760)
            .AddSrv(second, "daqifi-a4b1.local", 9760)
            .AddA("daqifi-95a7.local", IPAddress.Parse("192.168.1.39"))
            .AddA("daqifi-a4b1.local", IPAddress.Parse("192.168.1.40"))
            .Build();

        var devices = MapAdvertisement(packet);

        Assert.Equal(2, devices.Count);
        Assert.Equal(
            new[] { IPAddress.Parse("192.168.1.39"), IPAddress.Parse("192.168.1.40") },
            devices.Select(d => d.IPAddress));
    }

    [Fact]
    public void MapDevices_ParsesTxtAttributesCaseInsensitivelyAndKeepsTheFirstDuplicate()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement(
            txtStrings: ["SN=first", "sn=second", "PN=Nq1", "novalue", "=empty"]);

        var device = Assert.Single(MapAdvertisement(packet));

        Assert.Equal("first", device.SerialNumber);
        Assert.Equal(DeviceType.Nyquist1, device.Type);
        Assert.Equal(string.Empty, device.FirmwareVersion);
    }

    #endregion

    #region Local interface attribution

    [Fact]
    public void ResolveLocalInterfaceAddress_PicksTheInterfaceOnTheDeviceSubnet()
    {
        Assert.Equal(
            IPAddress.Parse("192.168.1.50"),
            MDnsDeviceFinder.ResolveLocalInterfaceAddress(IPAddress.Parse("192.168.1.39"), Interfaces));

        Assert.Equal(
            IPAddress.Parse("10.0.0.5"),
            MDnsDeviceFinder.ResolveLocalInterfaceAddress(IPAddress.Parse("10.0.0.200"), Interfaces));
    }

    [Fact]
    public void ResolveLocalInterfaceAddress_ReturnsNullWhenNoInterfaceCanReachTheDevice()
    {
        Assert.Null(MDnsDeviceFinder.ResolveLocalInterfaceAddress(IPAddress.Parse("172.16.4.9"), Interfaces));
        Assert.Null(MDnsDeviceFinder.ResolveLocalInterfaceAddress(IPAddress.IPv6Loopback, Interfaces));
    }

    #endregion

    #region Datagram handling

    [Fact]
    public void HandleDatagram_PublishesEachDeviceOnceAcrossRepeatedAnnouncements()
    {
        using var finder = new MDnsDeviceFinder();
        var announced = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => announced.Add(e.DeviceInfo);

        var discovered = new List<IDeviceInfo>();
        var packet = MDnsResponseBuilder.DeviceAdvertisement();

        // The query is retransmitted, so the same advertisement legitimately arrives repeatedly.
        finder.HandleDatagram(packet, packet.Length, DeviceEndPoint, Interfaces, discovered);
        finder.HandleDatagram(packet, packet.Length, DeviceEndPoint, Interfaces, discovered);

        Assert.Single(discovered);
        Assert.Single(announced);
        Assert.Equal("9090539562006014104", announced[0].SerialNumber);
    }

    [Fact]
    public void HandleDatagram_IgnoresQueriesAndMalformedTraffic()
    {
        using var finder = new MDnsDeviceFinder();
        var discovered = new List<IDeviceInfo>();

        var query = MDnsMessage.BuildPtrQuery(DaqifiService);
        finder.HandleDatagram(query, query.Length, DeviceEndPoint, Interfaces, discovered);
        finder.HandleDatagram([0x01, 0x02, 0x03], 3, DeviceEndPoint, Interfaces, discovered);

        Assert.Empty(discovered);
    }

    [Fact]
    public void HandleDatagram_IsNotDerailedByAThrowingSubscriber()
    {
        using var finder = new MDnsDeviceFinder();
        finder.DeviceDiscovered += (_, _) => throw new InvalidOperationException("subscriber blew up");

        var discovered = new List<IDeviceInfo>();
        var packet = MDnsResponseBuilder.DeviceAdvertisement();

        finder.HandleDatagram(packet, packet.Length, DeviceEndPoint, Interfaces, discovered);

        Assert.Single(discovered);
    }

    [Fact]
    public void HandleDatagram_HonoursACustomServiceType()
    {
        using var finder = new MDnsDeviceFinder("_daqifi-test._tcp");
        var discovered = new List<IDeviceInfo>();

        var packet = MDnsResponseBuilder.DeviceAdvertisement();
        finder.HandleDatagram(packet, packet.Length, DeviceEndPoint, Interfaces, discovered);

        Assert.Empty(discovered);
    }

    #endregion

    #region Construction and lifecycle

    [Fact]
    public void ServiceType_IsFullyQualifiedWithTheLocalDomain()
    {
        using var finder = new MDnsDeviceFinder();

        Assert.Equal("_daqifi._tcp.local", finder.ServiceType);
        Assert.Equal("_daqifi._tcp", MDnsDeviceFinder.DefaultServiceType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_daqifi")]
    public void Constructor_RejectsAMalformedServiceType(string serviceType)
    {
        Assert.Throws<ArgumentException>(() => new MDnsDeviceFinder(serviceType));
    }

    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesWithinTimeoutAndRaisesCompleted()
    {
        using var finder = new MDnsDeviceFinder();
        var completed = false;
        finder.DiscoveryCompleted += (_, _) => completed = true;

        var timeout = TimeSpan.FromMilliseconds(500);
        var startedAt = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.NotNull(devices);
        Assert.True(elapsed < timeout + TimeSpan.FromSeconds(2), $"took {elapsed}");
        Assert.True(completed);
    }

    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_ReturnsWithoutThrowing()
    {
        using var finder = new MDnsDeviceFinder();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var devices = await finder.DiscoverAsync(cts.Token);

        Assert.NotNull(devices);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var finder = new MDnsDeviceFinder();

        finder.Dispose();
        finder.Dispose();
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_Throws()
    {
        var finder = new MDnsDeviceFinder();
        finder.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync(CancellationToken.None));
    }

    #endregion

    private static IReadOnlyList<DeviceInfo> MapAdvertisement(byte[] packet)
    {
        Assert.True(MDnsMessage.TryParseResponse(packet, packet.Length, out var records));
        return MDnsDeviceFinder.MapDevices(records, DaqifiService, DeviceAddress, Interfaces);
    }
}
