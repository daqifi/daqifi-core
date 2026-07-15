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

    // LocationParentWalker: a HID collection's own PnP node never carries
    // DEVPKEY_Device_LocationInfo directly (confirmed on real bootloader hardware) — only its
    // parent USB device node does. These tests drive the walk with a fake query function so the
    // hop/normalization/termination logic is verified without any WMI/hardware dependency.

    [Fact]
    public void LocationParentWalker_Resolve_LocationOnFirstNode_ReturnsImmediately()
    {
        var result = LocationParentWalker.Resolve(
            "USB\\VID_04D8&PID_003C\\5&2C705BFE&0&1",
            _ => ("Port_#0001.Hub_#0001", null));

        Assert.Equal("Port_#0001.Hub_#0001", result);
    }

    [Fact]
    public void LocationParentWalker_Resolve_LocationOnParent_WalksUpOneHop()
    {
        const string hidChild = "HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000";
        const string usbParent = "USB\\VID_04D8&PID_003C\\5&2C705BFE&0&1";

        var result = LocationParentWalker.Resolve(hidChild, id => id switch
        {
            hidChild => (null, usbParent),
            usbParent => ("Port_#0001.Hub_#0001", null),
            _ => (null, null)
        });

        Assert.Equal("Port_#0001.Hub_#0001", result);
    }

    [Fact]
    public void LocationParentWalker_Resolve_ParentIdLowercase_IsNormalizedBeforeRequery()
    {
        // Real WMI DEVPKEY_Device_Parent values come back with lowercase hex segments (e.g.
        // "5&2c705bfe&0&1"), which fails InstanceIdRegex (uppercase-only) unless normalized
        // before the next hop's query — this is the exact bug this walker was introduced to fix.
        const string hidChild = "HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000";
        const string lowercaseParent = "USB\\VID_04D8&PID_003C\\5&2c705bfe&0&1";
        const string normalizedParent = "USB\\VID_04D8&PID_003C\\5&2C705BFE&0&1";

        var queriedIds = new List<string>();
        var result = LocationParentWalker.Resolve(hidChild, id =>
        {
            queriedIds.Add(id);
            return id == hidChild ? (null, lowercaseParent) : ("Port_#0001.Hub_#0001", null);
        });

        Assert.Equal("Port_#0001.Hub_#0001", result);
        Assert.Equal([hidChild, normalizedParent], queriedIds);
    }

    [Fact]
    public void LocationParentWalker_Resolve_EmptyStringLocation_IsTreatedAsNotFound()
    {
        // WMI can return an empty string (not null) for a property that exists on the node but
        // resolves to no value — must not be mistaken for a found location.
        var result = LocationParentWalker.Resolve(
            "HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000",
            _ => (string.Empty, null));

        Assert.Null(result);
    }

    [Fact]
    public void LocationParentWalker_Resolve_NoParent_ReturnsNull()
    {
        var result = LocationParentWalker.Resolve(
            "HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000",
            _ => (null, null));

        Assert.Null(result);
    }

    [Fact]
    public void LocationParentWalker_Resolve_MalformedParentId_StopsWalk()
    {
        var queriedIds = new List<string>();
        var result = LocationParentWalker.Resolve("HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000", id =>
        {
            queriedIds.Add(id);
            return (null, "not a valid instance id");
        });

        Assert.Null(result);
        Assert.Single(queriedIds);
    }

    [Fact]
    public void LocationParentWalker_Resolve_CyclicParentChain_StopsAtMaxHops()
    {
        // Defensive: a self-referential or cyclic parent chain must not spin forever.
        const string id = "HID\\VID_04D8&PID_003C\\6&353A92F2&0&0000";
        var callCount = 0;

        var result = LocationParentWalker.Resolve(id, _ =>
        {
            callCount++;
            return (null, id);
        });

        Assert.Null(result);
        Assert.Equal(LocationParentWalker.MaxParentHops + 1, callCount);
    }
}
