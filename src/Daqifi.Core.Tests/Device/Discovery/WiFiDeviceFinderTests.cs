using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Discovery;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Device.Discovery;

public class WiFiDeviceFinderTests
{
    // Issue #283: GetDeviceType had no "nq2" case, so a Nyquist2's part
    // number was silently reported as Unknown, same class of bug as
    // SerialDeviceFinder.ConvertDeviceType.
    [Theory]
    [InlineData("nq1", DeviceType.Nyquist1)]
    [InlineData("Nq1", DeviceType.Nyquist1)]
    [InlineData("nq2", DeviceType.Nyquist2)]
    [InlineData("Nq2", DeviceType.Nyquist2)]
    [InlineData("nq3", DeviceType.Nyquist3)]
    [InlineData("Nq3", DeviceType.Nyquist3)]
    [InlineData("nq4", DeviceType.Unknown)]
    [InlineData("", DeviceType.Unknown)]
    [InlineData(null, DeviceType.Unknown)]
    public void GetDeviceType_PartNumber_ReturnsCorrectType(string? partNumber, DeviceType expected)
    {
        var result = WiFiDeviceFinder.GetDeviceType(partNumber);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesWithinTimeout()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        var timeout = TimeSpan.FromSeconds(2);

        // Act
        var startTime = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotNull(devices);
        // Allow for some overhead, but should complete reasonably close to timeout
        Assert.True(elapsed.TotalSeconds <= timeout.TotalSeconds + 1);
    }

    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_CanBeCancelled()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        var devices = await finder.DiscoverAsync(cts.Token);

        // Assert
        Assert.NotNull(devices);
        // Should return empty or partial results when cancelled
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        // Arrange - Use port 0 to let system assign random port (avoid conflicts)
        using var finder = new WiFiDeviceFinder(0);
        var eventRaised = false;
        finder.DiscoveryCompleted += (sender, args) => eventRaised = true;

        // Act
        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void WiFiDeviceFinder_Dispose_DoesNotThrow()
    {
        // Arrange
        var finder = new WiFiDeviceFinder();

        // Act & Assert
        finder.Dispose();
        // Should not throw
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var finder = new WiFiDeviceFinder();
        finder.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }

    [Fact]
    public void WiFiDeviceFinder_DefaultConstructor_UsesPort30303()
    {
        // Arrange & Act
        using var finder = new WiFiDeviceFinder();

        // Assert
        // Should not throw and should use default port 30303
        Assert.NotNull(finder);
    }

    [Fact]
    public void WiFiDeviceFinder_CustomPort_AcceptsCustomPort()
    {
        // Arrange & Act
        using var finder = new WiFiDeviceFinder(12345);

        // Assert
        Assert.NotNull(finder);
    }

    // Virtual / tunnel adapter filter — issue #179.
    // WSL2 mirrored networking, Hyper-V vEthernet, VPN/TAP adapters frequently share a /24
    // subnet with the real WiFi/Ethernet NIC and cause Windows routing to pick the wrong egress
    // for broadcasts, silently breaking discovery.
    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Ethernet 3", "Hyper-V Virtual Ethernet Adapter #2")]
    [InlineData("Ethernet 4", "WSL Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1")]
    [InlineData("OpenVPN TAP-Windows6", "TAP-Windows Adapter V9")]
    // Keyword present only in name (not description) — both fields must be checked.
    [InlineData("vEthernet (Default Switch)", "")]
    [InlineData("WSL Bridge", "Generic Adapter")]
    [InlineData("VMware NAT", "Generic Adapter")]
    public void IsVirtualOrTunnelInterface_VirtualAdapters_ReturnsTrue(string name, string description)
    {
        Assert.True(WiFiDeviceFinder.IsVirtualOrTunnelInterface(name, description));
    }

    [Theory]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz")]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller")]
    [InlineData("Ethernet 2", "Intel(R) Ethernet Connection I219-LM")]
    [InlineData("WLAN", "Qualcomm Atheros QCA9377 Wireless Network Adapter")]
    public void IsVirtualOrTunnelInterface_PhysicalAdapters_ReturnsFalse(string name, string description)
    {
        Assert.False(WiFiDeviceFinder.IsVirtualOrTunnelInterface(name, description));
    }

    [Fact]
    public void IsVirtualOrTunnelInterface_NullInputs_ReturnsFalse()
    {
        Assert.False(WiFiDeviceFinder.IsVirtualOrTunnelInterface(null, null));
    }

    // Mixed-NIC selection — issue #179.
    // Simulates the realistic Windows multi-NIC scenario: physical WiFi + physical Ethernet +
    // WSL2 vEthernet + Hyper-V vEthernet + a down NIC + a non-IPv4 NIC + an unsupported
    // interface type. The filter must keep only the eligible physical adapters.
    [Fact]
    public void ShouldIncludeInterface_MixedNicList_ExcludesVirtualAndIneligible()
    {
        var nics = new (string Name, string Description, OperationalStatus Status, NetworkInterfaceType Type, bool IPv4, bool Expected)[]
        {
            ("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", OperationalStatus.Up, NetworkInterfaceType.Wireless80211, true, true),
            ("Ethernet", "Realtek PCIe GbE", OperationalStatus.Up, NetworkInterfaceType.Ethernet, true, true),
            ("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", OperationalStatus.Up, NetworkInterfaceType.Ethernet, true, false),
            ("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", OperationalStatus.Up, NetworkInterfaceType.Ethernet, true, false),
            ("VirtualBox Host-Only", "VirtualBox Host-Only Ethernet Adapter", OperationalStatus.Up, NetworkInterfaceType.Ethernet, true, false),
            ("Ethernet 2", "Disconnected adapter", OperationalStatus.Down, NetworkInterfaceType.Ethernet, true, false),
            ("Loopback", "Software Loopback", OperationalStatus.Up, NetworkInterfaceType.Loopback, true, false),
            ("Tunnel", "Teredo Tunneling", OperationalStatus.Up, NetworkInterfaceType.Tunnel, true, false),
            ("Ethernet 3", "Some IPv6-only adapter", OperationalStatus.Up, NetworkInterfaceType.Ethernet, false, false),
        };

        var included = nics
            .Where(n => WiFiDeviceFinder.ShouldIncludeInterface(n.Name, n.Description, n.Status, n.Type, n.IPv4))
            .Select(n => n.Name)
            .ToList();

        Assert.Equal(new[] { "Wi-Fi", "Ethernet" }, included);

        foreach (var nic in nics)
        {
            var actual = WiFiDeviceFinder.ShouldIncludeInterface(nic.Name, nic.Description, nic.Status, nic.Type, nic.IPv4);
            Assert.True(actual == nic.Expected, $"NIC '{nic.Name}' expected {nic.Expected} but got {actual}");
        }
    }

    // ---------------------------------------------------------------------
    // TCP data-port resolution (daqifi-core#244)
    // ---------------------------------------------------------------------

    [Fact]
    public void DefaultTcpDataPort_IsFirmwareDefault()
    {
        Assert.Equal(9760, Daqifi.Core.Device.DaqifiDeviceFactory.DefaultTcpDataPort);
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WithNullHost_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => WiFiDeviceFinder.ProbeTcpDataPortAsync(null!, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WithNonPositiveTimeout_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => WiFiDeviceFinder.ProbeTcpDataPortAsync(IPAddress.Loopback, TimeSpan.Zero));
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WithOutOfRangeDiscoveryPort_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => WiFiDeviceFinder.ProbeTcpDataPortAsync(IPAddress.Loopback, 70000, TimeSpan.FromSeconds(1)));
    }

    // The probe now binds the local socket to the discovery port itself (so it catches firmware that
    // replies to the well-known port). A test responder therefore occupies that port on IPAddress.Any,
    // which deterministically forces the probe's own bind to fail over to an ephemeral local port —
    // and the responder then replies to the probe's source endpoint, modelling firmware that answers
    // to the sender's port. Binding the responder on Any (not Loopback) guarantees the bind conflict.
    [Fact]
    public async Task ProbeTcpDataPortAsync_WhenHostAnswers_ReturnsAdvertisedDataPort()
    {
        const int advertisedPort = 12345;

        using var responder = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var discoveryPort = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        using var responderDone = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var replyBytes = BuildDiscoveryReply(devicePort: advertisedPort, hostName: "ProbeTestDevice");
        var responderTask = Task.Run(async () =>
        {
            var query = await responder.ReceiveAsync(responderDone.Token);
            await responder.SendAsync(replyBytes, replyBytes.Length, query.RemoteEndPoint);
        });

        var resolved = await WiFiDeviceFinder.ProbeTcpDataPortAsync(
            IPAddress.Loopback, discoveryPort, TimeSpan.FromSeconds(3));

        await responderTask;
        Assert.Equal(advertisedPort, resolved);
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WhenHostAdvertisesOutOfRangePort_ReturnsNull()
    {
        // A device reporting a DevicePort outside the valid 1..65535 TCP range is not a usable port;
        // the probe must not hand it back (which would defeat the caller's default-port fallback).
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var discoveryPort = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        using var responderDone = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var replyBytes = BuildDiscoveryReply(devicePort: 70000, hostName: "BadPortDevice");
        var responderTask = Task.Run(async () =>
        {
            var query = await responder.ReceiveAsync(responderDone.Token);
            await responder.SendAsync(replyBytes, replyBytes.Length, query.RemoteEndPoint);
        });

        var resolved = await WiFiDeviceFinder.ProbeTcpDataPortAsync(
            IPAddress.Loopback, discoveryPort, TimeSpan.FromMilliseconds(600));

        try { await responderTask; } catch (OperationCanceledException) { /* responder may outlive the probe */ }
        responderDone.Cancel();
        Assert.Null(resolved);
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WhenNoHostAnswers_ReturnsNull()
    {
        // Bind (but never read from) a socket so the port is occupied yet silent — nothing answers
        // the query, so the probe should time out and return null rather than throw.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var discoveryPort = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        var resolved = await WiFiDeviceFinder.ProbeTcpDataPortAsync(
            IPAddress.Loopback, discoveryPort, TimeSpan.FromMilliseconds(500));

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ProbeTcpDataPortAsync_WhenCallerCancels_ThrowsOperationCanceled()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var discoveryPort = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WiFiDeviceFinder.ProbeTcpDataPortAsync(
                IPAddress.Loopback, discoveryPort, TimeSpan.FromSeconds(5), cts.Token));
    }

    private static byte[] BuildDiscoveryReply(int devicePort, string hostName)
    {
        var message = new DaqifiOutMessage
        {
            HostName = hostName,
            DevicePort = (uint)devicePort,
            DeviceSn = 1,
            DevicePn = "nq1"
        };

        using var stream = new MemoryStream();
        message.WriteDelimitedTo(stream);
        return stream.ToArray();
    }
}
