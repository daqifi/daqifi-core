using System.Runtime.InteropServices;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Covers <see cref="WindowsUsbPortDescriptorProvider.Resolve"/> — the lookup that
/// <see cref="WindowsUsbPortDescriptorProvider.GetDescriptor"/> delegates to once past the Windows
/// platform gate. <see cref="WindowsPnpPortMap"/> takes its rows from an injected query, so this is
/// exercised against a fake map on any platform, without WMI. <c>Resolve</c> itself never touches
/// <c>System.Management</c>, so it carries no platform attribute and needs no CA1416 suppression to
/// call from here; only <see cref="GetDescriptor"/> does, once below.
/// </summary>
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
    public void Resolve_NullMap_ThrowsInsteadOfBeingSwallowed()
    {
        // The catch below exists to turn a map-rebuild failure into "unclassified", not to hide a
        // caller passing no map at all — that is a programmer error and must fail loudly rather
        // than read as an indistinguishable "no USB entity".
        Assert.Throws<ArgumentNullException>(() => WindowsUsbPortDescriptorProvider.Resolve("COM9", null!));
    }

    // ---- the platform gate ------------------------------------------------

    [Fact]
    public void GetDescriptor_OffWindows_ReturnsNullWithoutTouchingTheMap()
    {
        // Unlike LinuxUsbPortDescriptorProviderTests.GetDescriptor_OffLinux, there is no
        // unconditional twin of this test that also exercises GetDescriptor on Windows: doing so
        // would reach WindowsPnpPortMap.Shared, whose first lookup can rebuild the map and run a
        // real WMI query — slow and flaky in a unit-test run. The Windows-side lookup logic is
        // fully covered above through the injectable Resolve seam instead.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var provider = new WindowsUsbPortDescriptorProvider();

#pragma warning disable CA1416 // GetDescriptor is Windows-gated at runtime, not by this call site.
        Assert.Null(provider.GetDescriptor("COM9"));
#pragma warning restore CA1416
    }
}
