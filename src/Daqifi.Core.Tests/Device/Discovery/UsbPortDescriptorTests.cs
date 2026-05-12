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
}
