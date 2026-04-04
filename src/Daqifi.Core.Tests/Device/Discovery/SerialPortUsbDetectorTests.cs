using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class SerialPortUsbDetectorTests
{
    [Fact]
    public void IsDaqifiVendor_WithDaqifiVid_ReturnsTrue()
    {
        Assert.True(SerialPortUsbDetector.IsDaqifiVendor(0x04D8));
    }

    [Fact]
    public void IsDaqifiVendor_WithOtherVid_ReturnsFalse()
    {
        Assert.False(SerialPortUsbDetector.IsDaqifiVendor(0x1234));
        Assert.False(SerialPortUsbDetector.IsDaqifiVendor(0x0000));
        Assert.False(SerialPortUsbDetector.IsDaqifiVendor(0xFFFF));
    }

    [Fact]
    public void DaqifiVendorId_MatchesHidDeviceFinderVendorId()
    {
        // Ensure serial and HID discovery use the same vendor ID
        Assert.Equal(HidDeviceFinder.DefaultVendorId, SerialPortUsbDetector.DaqifiVendorId);
    }

    [Fact]
    public void GetPortUsbInfo_ReturnsNonNullDictionary()
    {
        // Should never throw; returns empty dictionary on failure
        var result = SerialPortUsbDetector.GetPortUsbInfo();
        Assert.NotNull(result);
    }

    [Fact]
    public void UsbId_RecordEquality()
    {
        var a = new SerialPortUsbDetector.UsbId(0x04D8, 0x003C);
        var b = new SerialPortUsbDetector.UsbId(0x04D8, 0x003C);
        var c = new SerialPortUsbDetector.UsbId(0x1234, 0x5678);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
