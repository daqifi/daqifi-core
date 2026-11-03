using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using System.IO;
using System.Net;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// The port a device reports for an external tool that has to open it (issue #842).
/// </summary>
/// <remarks>
/// The WiFi module flash tool is launched with the device's serial port. The port comes from the
/// device's serial transport and from nowhere else: a device's name is a display label, and its
/// default is "DAQiFi Device", which no operating system can open. These facts pin the device
/// side of that; <c>WifiModuleUpdaterTests</c> pins what the updater then does with it.
/// <para>
/// No port is opened here. Every transport is constructed and never connected, so this needs no
/// hardware.
/// </para>
/// </remarks>
public class DaqifiDeviceSerialPortNameTests
{
    [Theory]
    [InlineData("COM7")]
    [InlineData("/dev/ttyACM0")]
    public void SerialTransportDevice_ReportsThePortItIsOn_NotItsDisplayName(string portName)
    {
        using var transport = new SerialStreamTransport(portName);
        using var device = new DaqifiDevice(new DeviceConnectionOptions().DeviceName, transport);

        Assert.Equal("DAQiFi Device", device.Name);
        Assert.Equal(portName, ((ISerialPortNamed)device).SerialPortName);
    }

    [Fact]
    public void SerialTransportDevice_ReportsThePort_BeforeItIsEverConnected()
    {
        // The WiFi update resolves the port after it has disconnected the managed connection, so
        // a port that only answered while open would answer nothing at the moment it is needed.
        using var transport = new SerialStreamTransport("COM7");
        using var device = new DaqifiDevice("DAQiFi Device", transport);

        Assert.False(device.IsConnected);
        Assert.Equal("COM7", ((ISerialPortNamed)device).SerialPortName);
    }

    [Fact]
    public void TcpDevice_ReportsNoSerialPort()
    {
        // A real answer, not a gap: there is no serial port to hand a tool.
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 9760);
        using var device = new DaqifiDevice("DAQiFi Device", transport);

        Assert.Null(((ISerialPortNamed)device).SerialPortName);
    }

    [Fact]
    public void StreamOnlyDevice_ReportsNoSerialPort()
    {
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("DAQiFi Device", stream);

        Assert.Null(((ISerialPortNamed)device).SerialPortName);
    }
}
