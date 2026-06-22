using System.Runtime.InteropServices;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class HidPlatformFactoryTests
{
    [Fact]
    public void CreateForCurrentPlatform_ReturnsNonNullBackend()
    {
        var platform = HidPlatformFactory.CreateForCurrentPlatform();

        Assert.NotNull(platform);
    }

    [Fact]
    public void CreateForCurrentPlatform_SelectsBackendForOperatingSystem()
    {
        var platform = HidPlatformFactory.CreateForCurrentPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Reference by name so the macOS-only type is not bound on other
            // platforms (keeps the platform-compatibility analyzer satisfied).
            Assert.Equal("MacOsHidPlatform", platform.GetType().Name);
        }
        else
        {
            Assert.IsType<HidLibraryPlatform>(platform);
        }
    }
}
