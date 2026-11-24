using System.Net;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// What a <c>deviceId</c> is made of, and that it only ever stands for the unit it names
/// (issue #1042).
/// </summary>
public class DeviceIdTests
{
    [Fact]
    public void AnId_IsTheTransportAndTheSerialNumber()
    {
        Assert.Equal(
            ["wifi:9090539562", "serial:9090539562"],
            DaqifiAgent.MintIds([WiFi("9090539562"), Serial("9090539562")]));
    }

    [Fact]
    public void AnId_FollowsTheUnitToAnotherAddress()
    {
        Assert.Equal(
            DaqifiAgent.MintIds([Serial("42", port: "COM3"), WiFi("42", ip: "10.0.0.7")]),
            DaqifiAgent.MintIds([Serial("42", port: "COM9"), WiFi("42", ip: "10.0.0.99")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    // What Core reports for a status message that left the serial number out.
    [InlineData("0")]
    public void WithNoSerialNumber_AnIdFallsBackToWhereTheDeviceIs(string serialNumber)
    {
        Assert.Equal(
            ["serial:COM3", "wifi:aa-bb-cc-dd-ee-ff", "wifi:10.0.0.7"],
            DaqifiAgent.MintIds([
                Serial(serialNumber, port: "COM3"),
                WiFi(serialNumber, ip: "10.0.0.8", mac: "AA-BB-CC-DD-EE-FF"),
                WiFi(serialNumber, ip: "10.0.0.7", mac: null),
            ]));
    }

    [Fact]
    public void AnAllZeroMacAddress_IsNotUsedAsAName()
    {
        // The firmware's value for a radio it has not read yet, so every such unit shares it.
        Assert.Equal(["wifi:10.0.0.7"], DaqifiAgent.MintIds([WiFi("", ip: "10.0.0.7", mac: "00-00-00-00-00-00")]));
    }

    [Fact]
    public void TwoEntriesInOnePass_NeverShareAnId()
    {
        // One id standing for two connections would make connect_device's choice between them
        // invisible. The finders deduplicate, so this takes two units reporting one serial number.
        Assert.Equal(
            ["wifi:42", "wifi:00-11-22-33-44-55"],
            DaqifiAgent.MintIds([WiFi("42"), WiFi("42", ip: "10.0.0.9", mac: "00-11-22-33-44-55")]));
    }

    [Fact]
    public void AConnection_ToTheUnitTheIdNames_IsAccepted()
    {
        var (registry, device, requested) = Connected(connectedSerial: "42", requestedSerial: "42");

        var result = registry.Register(device, requested, "serial:42");

        Assert.Same(
            device, DaqifiAgent.AcceptConnection(registry, "serial:42", requested, result).Device);
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("0", "42")]
    [InlineData("42", "0")]
    public void AConnection_IsAcceptedWhenEitherSideReportsNoSerialNumber(string connectedSerial, string requestedSerial)
    {
        var (registry, device, requested) = Connected(connectedSerial, requestedSerial);

        var result = registry.Register(device, requested, "serial:X");

        Assert.Same(device, DaqifiAgent.AcceptConnection(registry, "serial:X", requested, result).Device);
    }

    [Fact]
    public void AConnection_ToAnotherUnitAtThatAddress_IsRefusedAndClosed()
    {
        // Unit 42 was discovered on this port, unplugged, and unit 43 plugged in. Filed under
        // serial:42, everything after this would drive the wrong hardware.
        var (registry, device, requested) = Connected(connectedSerial: "43", requestedSerial: "42");

        var result = registry.Register(device, requested, "serial:42");
        var ex = Assert.Throws<InvalidOperationException>(
            () => DaqifiAgent.AcceptConnection(registry, "serial:42", requested, result));

        Assert.Contains("43", ex.Message);
        Assert.Contains("discover_devices", ex.Message);
        Assert.Empty(registry.Devices);
        Assert.False(device.IsConnected);
    }

    [Theory]
    // The id is the MAC address because there is no serial number, and the lease moved on.
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "", "", "11-22-33-44-55-66")]
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "0", "0", "11-22-33-44-55-66")]
    // The unit there now reports a serial number; the id still stands for the MAC address.
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "0", "43", "11-22-33-44-55-66")]
    // The id is the serial number and the unit there reports none, so the MAC address decides.
    [InlineData("wifi:42", "42", "", "11-22-33-44-55-66")]
    // Two units reporting one serial number: the MAC address is what told them apart.
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "42", "42", "11-22-33-44-55-66")]
    public void AWiFiConnection_ToAnotherUnitAtThatAddress_IsRefusedAndClosed(
        string deviceId, string requestedSerial, string connectedSerial, string connectedMac)
    {
        var (registry, device, requested) = ConnectedOverWiFi(requestedSerial, connectedSerial, connectedMac);

        var result = registry.Register(device, requested, deviceId);
        var ex = Assert.Throws<InvalidOperationException>(
            () => DaqifiAgent.AcceptConnection(registry, deviceId, requested, result));

        Assert.Contains("11-22-33-44-55-66", ex.Message);
        Assert.Contains("discover_devices", ex.Message);
        Assert.Empty(registry.Devices);
        Assert.False(device.IsConnected);
    }

    [Theory]
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "", "", "aa:bb:cc:dd:ee:ff")]
    // Nothing to compare, or nothing that means anything.
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "", "", "")]
    [InlineData("wifi:aa-bb-cc-dd-ee-ff", "", "", "00-00-00-00-00-00")]
    // The id is the serial number, and it matches: a different MAC address is the same unit.
    [InlineData("wifi:42", "42", "42", "11-22-33-44-55-66")]
    public void AWiFiConnection_ToTheUnitTheIdNames_IsAccepted(
        string deviceId, string requestedSerial, string connectedSerial, string connectedMac)
    {
        var (registry, device, requested) = ConnectedOverWiFi(requestedSerial, connectedSerial, connectedMac);

        var result = registry.Register(device, requested, deviceId);

        Assert.Same(device, DaqifiAgent.AcceptConnection(registry, deviceId, requested, result).Device);
    }

    [Fact]
    public void ARefusedMacMismatch_LeavesAConnectionSomeoneElseOpenedAlone()
    {
        // Both units report serial number "0", which the registry takes for one unit and hands
        // back the first one's connection.
        var (registry, device, _) = ConnectedOverWiFi("0", "0", "11-22-33-44-55-66");
        registry.Register(device, deviceInfo: null, "wifi:11-22-33-44-55-66");
        var (_, second, requested) = ConnectedOverWiFi("0", "0", "AA-BB-CC-DD-EE-FF");

        var result = registry.Register(second, requested, "wifi:aa-bb-cc-dd-ee-ff");
        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Throws<InvalidOperationException>(
            () => DaqifiAgent.AcceptConnection(registry, "wifi:aa-bb-cc-dd-ee-ff", requested, result));

        Assert.True(device.IsConnected);
        Assert.Equal("wifi:11-22-33-44-55-66", Assert.Single(registry.Devices).Key);
    }

    [Fact]
    public void ARefusal_LeavesAConnectionSomeoneElseOpenedAlone()
    {
        // The same swap, but unit 43 is already connected under its own id: the registry hands
        // that connection back, and it is not this call's to close.
        var (registry, device, _) = Connected(connectedSerial: "43", requestedSerial: "43");
        registry.Register(device, deviceInfo: null, "serial:43");
        var (_, second, requested) = Connected(connectedSerial: "43", requestedSerial: "42");

        var result = registry.Register(second, requested, "wifi:42");
        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Throws<InvalidOperationException>(
            () => DaqifiAgent.AcceptConnection(registry, "wifi:42", requested, result));

        Assert.True(device.IsConnected);
        Assert.Equal("serial:43", Assert.Single(registry.Devices).Key);
    }

    private static (DaqifiDeviceRegistry Registry, FakeStreamingDevice Device, IDeviceInfo Requested) Connected(
        string connectedSerial, string requestedSerial)
    {
        var device = FakeStreamingDevice.CreateConnected();
        device.Metadata.SerialNumber = connectedSerial;
        return (new DaqifiDeviceRegistry(), device, Serial(requestedSerial));
    }

    private static (DaqifiDeviceRegistry Registry, FakeStreamingDevice Device, IDeviceInfo Requested) ConnectedOverWiFi(
        string requestedSerial, string connectedSerial, string connectedMac)
    {
        var device = FakeStreamingDevice.CreateConnected();
        device.Metadata.SerialNumber = connectedSerial;
        device.Metadata.MacAddress = connectedMac;
        return (new DaqifiDeviceRegistry(), device, WiFi(requestedSerial));
    }

    private static DeviceInfo Serial(string serialNumber, string port = "COM3") => new()
    {
        Name = "Nq1",
        SerialNumber = serialNumber,
        ConnectionType = ConnectionType.Serial,
        PortName = port,
    };

    private static DeviceInfo WiFi(string serialNumber, string ip = "10.0.0.7", string? mac = "AA-BB-CC-DD-EE-FF") => new()
    {
        Name = "NYQUIST",
        SerialNumber = serialNumber,
        ConnectionType = ConnectionType.WiFi,
        IPAddress = IPAddress.Parse(ip),
        MacAddress = mac,
    };
}
