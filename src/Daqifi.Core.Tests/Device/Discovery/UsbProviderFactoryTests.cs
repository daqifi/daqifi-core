using Daqifi.Core.Device.Discovery;
using System.Runtime.InteropServices;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Tests for the two platform factories that decide which USB descriptor and location providers a
/// discovery sweep gets (slice 3 of #464). Neither had any test.
/// </summary>
/// <remarks>
/// These are short, but they are the only thing standing between a platform losing its provider and
/// nobody noticing: the fallbacks are silent by design — a null provider answers null for every
/// port, which discovery reads as "unknown, probe it anyway". A platform that quietly dropped to
/// the fallback would still find devices, just slowly, which is exactly the regression #487 is
/// about. Written in the style of <c>HidPlatformFactoryTests</c>: assert the branch for the
/// platform the test run is actually on, so every supported OS is covered by the OS that runs it.
/// </remarks>
public class UsbProviderFactoryTests
{
    [Fact]
    public void UsbPortDescriptorProviderFactory_ReturnsAProviderForEveryPlatform()
    {
        Assert.NotNull(UsbPortDescriptorProviderFactory.CreateForCurrentPlatform());
    }

    [Fact]
    public void UsbPortDescriptorProviderFactory_SelectsTheProviderForThisOperatingSystem()
    {
        var provider = UsbPortDescriptorProviderFactory.CreateForCurrentPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Referenced by name so the Windows-only type is not bound on other platforms.
            Assert.Equal("WindowsUsbPortDescriptorProvider", provider.GetType().Name);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.IsType<LinuxUsbPortDescriptorProvider>(provider);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsType<MacOsUsbPortDescriptorProvider>(provider);
        }
        else
        {
            Assert.Same(NullUsbPortDescriptorProvider.Instance, provider);
        }
    }

    [Fact]
    public void UsbLocationProviderFactory_ReturnsAProviderForEveryPlatform()
    {
        Assert.NotNull(UsbLocationProviderFactory.CreateForCurrentPlatform());
    }

    [Fact]
    public void UsbLocationProviderFactory_ResolvesLocationsOnWindowsAndFallsBackElsewhere()
    {
        // Physical-location correlation is Windows-only in v1 — everywhere else the fallback is
        // the answer, and consumers must see "no known location" rather than a wrong one.
        var provider = UsbLocationProviderFactory.CreateForCurrentPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal("WindowsUsbLocationProvider", provider.GetType().Name);
        }
        else
        {
            Assert.Same(NullUsbLocationProvider.Instance, provider);
        }
    }
}
