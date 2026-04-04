using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class SerialDeviceFinderFilterTests
{
    #region FilterProbableDaqifiPorts

    [Fact]
    public void FilterProbableDaqifiPorts_ExcludesBluetoothPorts()
    {
        var ports = new[] { "COM1", "COM3", "/dev/tty.Bluetooth-Incoming-Port" };
        var result = SerialDeviceFinder.FilterProbableDaqifiPorts(ports).ToList();

        Assert.DoesNotContain(result, p => p.Contains("Bluetooth"));
    }

    [Fact]
    public void FilterProbableDaqifiPorts_ExcludesDebugPorts()
    {
        var ports = new[] { "COM1", "/dev/cu.debug-console" };
        var result = SerialDeviceFinder.FilterProbableDaqifiPorts(ports).ToList();

        Assert.DoesNotContain(result, p => p.Contains("debug"));
    }

    [Fact]
    public void FilterProbableDaqifiPorts_ExcludesWlanPorts()
    {
        var ports = new[] { "COM1", "/dev/cu.wlan-debug" };
        var result = SerialDeviceFinder.FilterProbableDaqifiPorts(ports).ToList();

        Assert.DoesNotContain(result, p => p.Contains("wlan"));
    }

    [Fact]
    public void FilterProbableDaqifiPorts_EmptyInput_ReturnsEmpty()
    {
        var result = SerialDeviceFinder.FilterProbableDaqifiPorts(Array.Empty<string>()).ToList();
        Assert.Empty(result);
    }

    #endregion

    #region FilterByUsbVidPid

    [Fact]
    public void FilterByUsbVidPid_EmptyUsbInfo_ReturnsAllPorts()
    {
        var ports = new[] { "COM1", "COM2", "COM3" };
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>();

        var result = SerialDeviceFinder.FilterByUsbVidPid(ports, usbInfo).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(ports, result);
    }

    [Fact]
    public void FilterByUsbVidPid_KeepsDaqifiVendorPorts()
    {
        var ports = new[] { "COM1", "COM2" };
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>
        {
            ["COM1"] = new(SerialPortUsbDetector.DaqifiVendorId, 0x003C),
            ["COM2"] = new(SerialPortUsbDetector.DaqifiVendorId, 0x0042)
        };

        var result = SerialDeviceFinder.FilterByUsbVidPid(ports, usbInfo).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains("COM1", result);
        Assert.Contains("COM2", result);
    }

    [Fact]
    public void FilterByUsbVidPid_RejectsNonDaqifiVendorPorts()
    {
        var ports = new[] { "COM1", "COM2", "COM3" };
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>
        {
            ["COM1"] = new(0x1234, 0x5678), // Random USB device
            ["COM2"] = new(SerialPortUsbDetector.DaqifiVendorId, 0x003C), // DAQiFi
            ["COM3"] = new(0x2341, 0x0043)  // Arduino
        };

        var result = SerialDeviceFinder.FilterByUsbVidPid(ports, usbInfo).ToList();

        Assert.Single(result);
        Assert.Contains("COM2", result);
    }

    [Fact]
    public void FilterByUsbVidPid_KeepsPortsWithNoUsbInfo()
    {
        // Ports not in usbInfo are kept (could be non-USB serial or detection missed them)
        var ports = new[] { "COM1", "COM2", "COM3" };
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>
        {
            ["COM1"] = new(0x1234, 0x5678) // Known non-DAQiFi
        };

        var result = SerialDeviceFinder.FilterByUsbVidPid(ports, usbInfo).ToList();

        // COM1 rejected (known non-DAQiFi), COM2 and COM3 kept (unknown = safe to probe)
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain("COM1", result);
        Assert.Contains("COM2", result);
        Assert.Contains("COM3", result);
    }

    [Fact]
    public void FilterByUsbVidPid_MixedScenario()
    {
        var ports = new[] { "/dev/cu.usbmodem101", "/dev/cu.usbserial-1420", "/dev/cu.SLAB_USBtoUART", "COM4" };
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>
        {
            ["/dev/cu.usbmodem101"] = new(SerialPortUsbDetector.DaqifiVendorId, 0x003C), // DAQiFi
            ["/dev/cu.usbserial-1420"] = new(0x0403, 0x6001), // FTDI chip
            ["/dev/cu.SLAB_USBtoUART"] = new(0x10C4, 0xEA60)  // Silicon Labs
            // COM4 not in usbInfo
        };

        var result = SerialDeviceFinder.FilterByUsbVidPid(ports, usbInfo).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains("/dev/cu.usbmodem101", result); // DAQiFi VID match
        Assert.Contains("COM4", result);                 // Unknown = kept
    }

    [Fact]
    public void FilterByUsbVidPid_EmptyInput_ReturnsEmpty()
    {
        var usbInfo = new Dictionary<string, SerialPortUsbDetector.UsbId>
        {
            ["COM1"] = new(SerialPortUsbDetector.DaqifiVendorId, 0x003C)
        };

        var result = SerialDeviceFinder.FilterByUsbVidPid(Enumerable.Empty<string>(), usbInfo).ToList();
        Assert.Empty(result);
    }

    #endregion
}
