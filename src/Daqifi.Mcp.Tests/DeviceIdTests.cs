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
