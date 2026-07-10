using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class UsbLocationProviderTests
{
    [Fact]
    public void NullUsbLocationProvider_AlwaysReturnsNull()
    {
        var provider = NullUsbLocationProvider.Instance;
        Assert.Null(provider.GetLocationKey("COM9"));
        Assert.Null(provider.GetLocationKey(@"\\?\hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000#{4d1e55b2-f46c-11d0-894f-00a0c90c8b6e}"));
        Assert.Null(provider.GetLocationKey(""));
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_TypicalHidPath_ReturnsExpectedInstanceId()
    {
        const string devicePath =
            @"\\?\hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000#{4d1e55b2-f46c-11d0-894f-00a0c90c8b6e}";

        var result = HidDevicePathParser.ParseInstanceId(devicePath);

        Assert.Equal(@"HID\VID_04D8&PID_003C\7&1A2B3C4D&0&0000", result);
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_LowercaseInput_IsUppercased()
    {
        const string devicePath =
            @"\\?\hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000#{4d1e55b2-f46c-11d0-894f-00a0c90c8b6e}";

        var result = HidDevicePathParser.ParseInstanceId(devicePath);

        Assert.Equal(result, result?.ToUpperInvariant());
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_WithoutLeadingPrefix_StillParses()
    {
        // Defensive: some libraries surface the path without the "\\?\" prefix.
        const string devicePath =
            @"hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000#{4d1e55b2-f46c-11d0-894f-00a0c90c8b6e}";

        var result = HidDevicePathParser.ParseInstanceId(devicePath);

        Assert.Equal(@"HID\VID_04D8&PID_003C\7&1A2B3C4D&0&0000", result);
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_MissingGuidSegment_ReturnsNull()
    {
        const string devicePath = @"\\?\hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000";

        var result = HidDevicePathParser.ParseInstanceId(devicePath);

        Assert.Null(result);
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_NoHashSeparators_ReturnsNull()
    {
        // e.g. a plain COM port name reaching the HID-path parser by mistake.
        var result = HidDevicePathParser.ParseInstanceId("COM9");

        Assert.Null(result);
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_EmptyInput_ReturnsNull()
    {
        Assert.Null(HidDevicePathParser.ParseInstanceId(string.Empty));
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_NullInput_ReturnsNull()
    {
        Assert.Null(HidDevicePathParser.ParseInstanceId(null!));
    }

    [Fact]
    public void HidDevicePathParser_ParseInstanceId_GuidSegmentNotBraced_ReturnsNull()
    {
        // Trailing segment must look like "{guid}"; anything else means this isn't a device
        // interface path in the expected shape.
        const string devicePath = @"\\?\hid#vid_04d8&pid_003c#7&1a2b3c4d&0&0000#not-a-guid";

        var result = HidDevicePathParser.ParseInstanceId(devicePath);

        Assert.Null(result);
    }
}
