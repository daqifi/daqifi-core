using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Covers <see cref="WindowsUsbPortDescriptorProvider.Resolve"/> — the lookup that
/// <see cref="WindowsUsbPortDescriptorProvider.GetDescriptor"/> delegates to once past the Windows
/// platform gate. <see cref="WindowsPnpPortMap"/> takes its rows from an injected query, so this is
/// exercised against a fake map on any platform, without WMI.
/// </summary>
/// <remarks>
/// <see cref="WindowsUsbPortDescriptorProvider"/> carries <c>[SupportedOSPlatform("windows")]</c>
/// because its production path (<see cref="WindowsPnpPortMap.Shared"/>) ultimately reaches WMI, but
/// <see cref="WindowsUsbPortDescriptorProvider.Resolve"/> itself never touches
/// <c>System.Management</c> — it only reads from whatever <see cref="WindowsPnpPortMap"/> it is
/// given, which is not itself platform-gated. CA1416 is suppressed the same way
/// <see cref="UsbPortDescriptorProviderFactory"/> already suppresses it to construct this type: the
/// call sites here never reach the Windows-only branch.
/// </remarks>
#pragma warning disable CA1416
public class WindowsUsbPortDescriptorProviderTests
{
    private static PnpPortEntity Usb(string port, string vidPid = "VID_04D8&PID_F794") =>
        new($@"USB\{vidPid}\SERIAL{port}", $"USB Serial Device ({port})");

    private static WindowsPnpPortMap MapOf(params PnpPortEntity[] entities) =>
        new(() => entities);

    [Fact]
    public void Resolve_PortWithUsbEntity_ReturnsItsVidPid()
    {
        var map = MapOf(Usb("COM9"));

        var descriptor = WindowsUsbPortDescriptorProvider.Resolve("COM9", map);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), descriptor);
    }

    [Fact]
    public void Resolve_PortTheMapDoesNotList_ReturnsNull()
    {
        var map = MapOf(Usb("COM9"));

        Assert.Null(WindowsUsbPortDescriptorProvider.Resolve("COM4", map));
    }

    [Fact]
    public void Resolve_PortClaimedOnlyByANonUsbEntity_ReturnsNull()
    {
        // A motherboard COM port's PnP entity has no VID/PID at all.
        var map = MapOf(new PnpPortEntity(@"ACPI\PNP0501\1", "Communications Port (COM1)"));

        Assert.Null(WindowsUsbPortDescriptorProvider.Resolve("COM1", map));
    }

    [Fact]
    public void Resolve_PortClaimedByBothANonUsbAndAUsbEntity_SkipsToTheUsbOne()
    {
        // Mirrors WindowsPnpPortMapTests' "two entities claim one port" case: both are kept, in
        // enumeration order, and this is the caller that wants the first one carrying a VID/PID.
        var map = MapOf(new PnpPortEntity(@"ACPI\PNP0501\1", "Legacy bridge (COM9)"), Usb("COM9"));

        var descriptor = WindowsUsbPortDescriptorProvider.Resolve("COM9", map);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), descriptor);
    }

    [Fact]
    public void Resolve_MapRebuildThrows_ReturnsNullInsteadOfPropagating()
    {
        // A WMI error surfaces from WindowsPnpPortMap.GetDeviceIds as a thrown exception (see
        // WindowsPnpPortMapTests.AFailedRebuild_LeavesTheLastGoodMapInPlace); the descriptor
        // provider must behave like the no-op provider for this port rather than propagate it, so
        // the caller falls through to legacy probe behavior.
        var map = new WindowsPnpPortMap(() => throw new InvalidOperationException("WMI unavailable"));

        Assert.Null(WindowsUsbPortDescriptorProvider.Resolve("COM9", map));
    }

    [Fact]
    public void GetDescriptor_OffWindows_ReturnsNullWithoutTouchingTheMap()
    {
        var provider = new WindowsUsbPortDescriptorProvider();

        // This assembly's tests run on macOS/Linux CI as well as Windows; off Windows the platform
        // gate must short-circuit before WindowsPnpPortMap.Shared (which would otherwise attempt a
        // WMI query) is ever reached.
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Assert.Null(provider.GetDescriptor("COM9"));
        }
    }
}
#pragma warning restore CA1416
