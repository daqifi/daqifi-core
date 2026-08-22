using Daqifi.Core.Device.Discovery;
using System.Runtime.InteropServices;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Tests for the macOS <c>ioreg</c> USB descriptor provider, driven against canned <c>ioreg</c>
/// output rather than the real <c>/usr/sbin/ioreg</c> (slice 3 of #464).
/// </summary>
/// <remarks>
/// This provider had no tests at all, despite parsing external-tool output the same way the
/// firmware and discovery collaborators covered elsewhere in #464 do. <see cref="MacOsUsbPortDescriptorProvider.Parse"/>
/// is exposed <c>internal</c> specifically so its text-parsing logic can be pinned down without
/// spawning a real process — <see cref="MacOsUsbPortDescriptorProvider.GetDescriptor"/> itself is left
/// untested here beyond the platform gate, since exercising it for real would mean either running on
/// macOS and depending on whatever USB devices happen to be attached to the CI runner, or mocking out
/// process execution that the provider does not expose a seam for.
/// </remarks>
public class MacOsUsbPortDescriptorProviderTests
{
    #region Parse — the happy path

    [Fact]
    public void Parse_SingleDeviceWithCalloutAfterVidPid_MapsPortToDescriptor()
    {
        var output = string.Join('\n',
            "+-o AppleUSBDevice",
            "  | {",
            "  |   \"idVendor\" = 1240",
            "  |   \"idProduct\" = 63380",
            "  |   \"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"",
            "  | }");

        var result = MacOsUsbPortDescriptorProvider.Parse(output);

        Assert.Equal(new UsbPortDescriptor(1240, 63380), result["/dev/cu.usbmodem1234561"]);
    }

    [Fact]
    public void Parse_MultipleDevices_EachGetsItsOwnDescriptor()
    {
        var output = string.Join('\n',
            "\"idVendor\" = 1240",
            "\"idProduct\" = 63380",
            "\"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"",
            "\"idVendor\" = 6790",
            "\"idProduct\" = 29987",
            "\"IOCalloutDevice\" = \"/dev/cu.usbserial-1420\"");

        var result = MacOsUsbPortDescriptorProvider.Parse(output);

        Assert.Equal(2, result.Count);
        Assert.Equal(new UsbPortDescriptor(1240, 63380), result["/dev/cu.usbmodem1234561"]);
        Assert.Equal(new UsbPortDescriptor(6790, 29987), result["/dev/cu.usbserial-1420"]);
    }

    [Fact]
    public void Parse_VidPidCarryForwardToMultipleCalloutsUnderTheSameNode()
    {
        // A composite device can expose more than one IOSerialBSDClient child under the same USB
        // node — e.g. a dual-UART bridge. ioreg prints the parent's idVendor/idProduct once, before
        // both children, so both callouts should pick up the same descriptor.
        var output = string.Join('\n',
            "\"idVendor\" = 1240",
            "\"idProduct\" = 63380",
            "\"IOCalloutDevice\" = \"/dev/cu.usbserial-1420A\"",
            "\"IOCalloutDevice\" = \"/dev/cu.usbserial-1420B\"");

        var result = MacOsUsbPortDescriptorProvider.Parse(output);

        Assert.Equal(2, result.Count);
        Assert.Equal(new UsbPortDescriptor(1240, 63380), result["/dev/cu.usbserial-1420A"]);
        Assert.Equal(new UsbPortDescriptor(1240, 63380), result["/dev/cu.usbserial-1420B"]);
    }

    #endregion

    #region Parse — malformed or absent input

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyMap()
    {
        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(""));
    }

    [Fact]
    public void Parse_Null_ReturnsEmptyMapRatherThanThrowing()
    {
        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(null!));
    }

    [Fact]
    public void Parse_CalloutBeforeAnyVidPidHasBeenSeen_IsNotMapped()
    {
        // A callout device that appears before any idVendor/idProduct line has nothing to
        // associate with — e.g. a non-USB serial node ioreg happens to also report.
        var output = "\"IOCalloutDevice\" = \"/dev/cu.Bluetooth-Incoming-Port\"";

        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(output));
    }

    [Fact]
    public void Parse_VidWithoutAMatchingPid_LeavesTheCalloutUnmapped()
    {
        var output = string.Join('\n',
            "\"idVendor\" = 1240",
            "\"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"");

        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(output));
    }

    [Fact]
    public void Parse_PidWithoutAMatchingVid_LeavesTheCalloutUnmapped()
    {
        var output = string.Join('\n',
            "\"idProduct\" = 63380",
            "\"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"");

        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(output));
    }

    [Fact]
    public void Parse_DeviceWithNoCalloutDevice_ContributesNothingToTheMap()
    {
        // Not every USB device node ioreg reports is a serial device — most have no
        // IOCalloutDevice property at all.
        var output = string.Join('\n',
            "\"idVendor\" = 1240",
            "\"idProduct\" = 63380");

        Assert.Empty(MacOsUsbPortDescriptorProvider.Parse(output));
    }

    [Fact]
    public void Parse_LaterDeviceOverwritesAnEarlierCalloutForTheSamePath()
    {
        // Not expected in real ioreg output (paths are unique per device), but the parser is a
        // single forward pass with no dedup, so document that "last write wins" is what it does.
        var output = string.Join('\n',
            "\"idVendor\" = 1240",
            "\"idProduct\" = 63380",
            "\"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"",
            "\"idVendor\" = 6790",
            "\"idProduct\" = 29987",
            "\"IOCalloutDevice\" = \"/dev/cu.usbmodem1234561\"");

        var result = MacOsUsbPortDescriptorProvider.Parse(output);

        Assert.Equal(new UsbPortDescriptor(6790, 29987), result["/dev/cu.usbmodem1234561"]);
    }

    #endregion

    #region The platform gate

    [Fact]
    public void GetDescriptor_OffMacOS_AnswersNullForEveryPort()
    {
        // Elsewhere ioreg either does not exist or means something else, so the gate has to
        // produce the answer without ever spawning a process. On macOS the real ioreg-backed path
        // is exercised manually rather than in CI, for the reasons in the type-level remarks.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        var provider = new MacOsUsbPortDescriptorProvider();

        Assert.Null(provider.GetDescriptor("/dev/cu.usbmodem101"));
        Assert.Null(provider.GetDescriptor("ttyACM0"));
        Assert.Null(provider.GetDescriptor("COM9"));
    }

    #endregion
}
