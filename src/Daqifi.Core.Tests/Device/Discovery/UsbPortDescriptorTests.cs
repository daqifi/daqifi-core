using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class UsbPortDescriptorTests
{
    [Fact]
    public void DaqifiUsbIds_VendorAndCdcProductId_MatchExpectedValues()
    {
        // Locks the published constants. If these change without
        // explicit intent, every consumer's USB filter breaks.
        Assert.Equal(0x04D8, DaqifiUsbIds.VendorId);
        Assert.Equal(0xF794, DaqifiUsbIds.CdcProductId);
    }

    [Fact]
    public void DaqifiUsbIds_IsDaqifiCdcDevice_TrueForExactMatch()
    {
        var descriptor = new UsbPortDescriptor(0x04D8, 0xF794);
        Assert.True(DaqifiUsbIds.IsDaqifiCdcDevice(descriptor));
    }

    [Fact]
    public void DaqifiUsbIds_IsDaqifiCdcDevice_FalseForBootloaderPid()
    {
        // Bootloader mode PID — not the CDC mode we discover via SerialDeviceFinder.
        var descriptor = new UsbPortDescriptor(0x04D8, 0x003C);
        Assert.False(DaqifiUsbIds.IsDaqifiCdcDevice(descriptor));
    }

    [Fact]
    public void DaqifiUsbIds_IsDaqifiCdcDevice_FalseForOtherVendor()
    {
        // CH340 vendor — common USB serial chip; must not be classified as DAQiFi.
        var descriptor = new UsbPortDescriptor(0x1A86, 0x7523);
        Assert.False(DaqifiUsbIds.IsDaqifiCdcDevice(descriptor));
    }

    [Fact]
    public void NullUsbPortDescriptorProvider_AlwaysReturnsNull()
    {
        var provider = NullUsbPortDescriptorProvider.Instance;
        Assert.Null(provider.GetDescriptor("COM9"));
        Assert.Null(provider.GetDescriptor("/dev/ttyACM1"));
        Assert.Null(provider.GetDescriptor(""));
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_EmptyInput_ReturnsEmpty()
    {
        var result = MacOsUsbPortDescriptorProvider.Parse(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_SingleDaqifiDevice_MapsCorrectly()
    {
        const string ioregOutput = """
              | {
              |   "idVendor" = 1240
              |   "idProduct" = 63380
              |   {
              |     "IOCalloutDevice" = "/dev/cu.usbmodem101"
              |   }
              | }
            """;

        var result = MacOsUsbPortDescriptorProvider.Parse(ioregOutput);

        Assert.Single(result);
        Assert.True(result.ContainsKey("/dev/cu.usbmodem101"));
        Assert.Equal(1240, result["/dev/cu.usbmodem101"].VendorId);   // 0x04D8
        Assert.Equal(63380, result["/dev/cu.usbmodem101"].ProductId); // 0xF794
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_MultipleDevices_MapsAll()
    {
        const string ioregOutput = """
              | {
              |   "idVendor" = 1240
              |   "idProduct" = 63380
              |   {
              |     "IOCalloutDevice" = "/dev/cu.usbmodem101"
              |   }
              | }
              | {
              |   "idVendor" = 1027
              |   "idProduct" = 24577
              |   {
              |     "IOCalloutDevice" = "/dev/cu.usbserial-1420"
              |   }
              | }
            """;

        var result = MacOsUsbPortDescriptorProvider.Parse(ioregOutput);

        Assert.Equal(2, result.Count);
        Assert.Equal(1240, result["/dev/cu.usbmodem101"].VendorId);
        Assert.Equal(1027, result["/dev/cu.usbserial-1420"].VendorId);
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_DeviceWithoutCallout_NotMapped()
    {
        const string ioregOutput = """
              | {
              |   "idVendor" = 1240
              |   "idProduct" = 63380
              | }
            """;

        var result = MacOsUsbPortDescriptorProvider.Parse(ioregOutput);
        Assert.Empty(result);
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_CalloutWithoutVidPid_NotMapped()
    {
        const string ioregOutput = """
              | {
              |   "IOCalloutDevice" = "/dev/cu.usbmodem101"
              | }
            """;

        var result = MacOsUsbPortDescriptorProvider.Parse(ioregOutput);
        Assert.Empty(result);
    }

    [Fact]
    public void MacOsUsbPortDescriptorProvider_Parse_VidPidCarryOverToNextCallout()
    {
        // A single USB device may expose multiple serial interfaces. Both
        // callout devices should get the same VID/PID from the parent node.
        const string ioregOutput = """
              | {
              |   "idVendor" = 1240
              |   "idProduct" = 63380
              |   {
              |     "IOCalloutDevice" = "/dev/cu.usbmodem1011"
              |   }
              |   {
              |     "IOCalloutDevice" = "/dev/cu.usbmodem1013"
              |   }
              | }
            """;

        var result = MacOsUsbPortDescriptorProvider.Parse(ioregOutput);

        Assert.Equal(2, result.Count);
        Assert.Equal(1240, result["/dev/cu.usbmodem1011"].VendorId);
        Assert.Equal(1240, result["/dev/cu.usbmodem1013"].VendorId);
    }

    [Fact]
    public void PnpDeviceIdParser_ParseVidPid_UsbSerialDeviceId_ReturnsDescriptor()
    {
        var descriptor = PnpDeviceIdParser.ParseVidPid(@"USB\VID_04D8&PID_F794\5&1A2B3C4D&0&2");

        Assert.NotNull(descriptor);
        Assert.Equal(0x04D8, descriptor.VendorId);
        Assert.Equal(0xF794, descriptor.ProductId);
    }

    [Fact]
    public void PnpDeviceIdParser_ParseVidPid_LowercaseKeys_StillParses()
    {
        // Some drivers report "Vid_" / "Pid_".
        var descriptor = PnpDeviceIdParser.ParseVidPid(@"usb\vid_04d8&pid_f794\5&1a2b3c4d&0&2");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), descriptor);
    }

    [Fact]
    public void PnpDeviceIdParser_ParseVidPid_NonUsbDeviceId_ReturnsNull()
    {
        // A motherboard serial port and a Bluetooth SPP port both live under PNPClass='Ports'
        // but carry no VID/PID, and must stay unclassified rather than be misread.
        Assert.Null(PnpDeviceIdParser.ParseVidPid(@"ACPI\PNP0501\1"));
        Assert.Null(PnpDeviceIdParser.ParseVidPid(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&2"));
    }

    [Fact]
    public void PnpDeviceIdParser_ParseVidPid_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(PnpDeviceIdParser.ParseVidPid(null));
        Assert.Null(PnpDeviceIdParser.ParseVidPid(""));
    }

    [Fact]
    public void PnpDeviceIdParser_SelectUsbDescriptor_SkipsIdsWithoutVidPid()
    {
        // A port claimed by both a non-USB entity and a USB one is still classified — the same
        // "keep looking" behavior the per-port WMI query had when it walked its result set.
        var descriptor = PnpDeviceIdParser.SelectUsbDescriptor(
            [@"ACPI\PNP0501\1", @"USB\VID_04D8&PID_F794\5&1A2B3C4D&0&2"]);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), descriptor);
    }

    [Fact]
    public void PnpDeviceIdParser_SelectUsbDescriptor_NoIdsOrNoneUsb_ReturnsNull()
    {
        Assert.Null(PnpDeviceIdParser.SelectUsbDescriptor([]));
        Assert.Null(PnpDeviceIdParser.SelectUsbDescriptor([@"ACPI\PNP0501\1"]));
    }
}
