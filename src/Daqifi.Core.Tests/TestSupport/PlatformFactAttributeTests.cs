using System.Reflection;
using System.Runtime.InteropServices;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Covers the platform gate that replaced the three bare <c>return</c>s in the USB descriptor
/// provider tests (issue #663). The decision itself is pure — <see cref="PlatformFactAttribute.ShouldSkip"/>
/// takes the current platform as an argument — so every arm of it is exercised on every CI leg,
/// which is exactly the property the gate exists to give the tests it guards.
/// </summary>
public class PlatformFactAttributeTests
{
    #region The skip decision, on every platform rather than the running one

    [Theory]
    [InlineData(TestPlatforms.Windows, TestPlatforms.Windows)]
    [InlineData(TestPlatforms.Linux, TestPlatforms.Linux)]
    [InlineData(TestPlatforms.MacOS, TestPlatforms.MacOS)]
    public void ShouldSkip_PlatformIsNamed_Skips(TestPlatforms skipOn, TestPlatforms current)
    {
        Assert.True(PlatformFactAttribute.ShouldSkip(skipOn, current));
    }

    [Theory]
    [InlineData(TestPlatforms.Windows, TestPlatforms.Linux)]
    [InlineData(TestPlatforms.Windows, TestPlatforms.MacOS)]
    [InlineData(TestPlatforms.Linux, TestPlatforms.Windows)]
    [InlineData(TestPlatforms.Linux, TestPlatforms.MacOS)]
    [InlineData(TestPlatforms.MacOS, TestPlatforms.Windows)]
    [InlineData(TestPlatforms.MacOS, TestPlatforms.Linux)]
    public void ShouldSkip_PlatformIsNotNamed_Runs(TestPlatforms skipOn, TestPlatforms current)
    {
        Assert.False(PlatformFactAttribute.ShouldSkip(skipOn, current));
    }

    [Theory]
    [InlineData(TestPlatforms.Windows)]
    [InlineData(TestPlatforms.Linux)]
    [InlineData(TestPlatforms.MacOS)]
    public void ShouldSkip_SeveralPlatformsNamed_SkipsOnEachOfThem(TestPlatforms current)
    {
        var all = TestPlatforms.Windows | TestPlatforms.Linux | TestPlatforms.MacOS;

        Assert.True(PlatformFactAttribute.ShouldSkip(all, current));
    }

    [Fact]
    public void ShouldSkip_TwoOfThreeNamed_RunsOnTheThird()
    {
        var windowsAndMac = TestPlatforms.Windows | TestPlatforms.MacOS;

        Assert.False(PlatformFactAttribute.ShouldSkip(windowsAndMac, TestPlatforms.Linux));
        Assert.True(PlatformFactAttribute.ShouldSkip(windowsAndMac, TestPlatforms.Windows));
        Assert.True(PlatformFactAttribute.ShouldSkip(windowsAndMac, TestPlatforms.MacOS));
    }

    [Fact]
    public void ShouldSkip_NamesNoPlatform_NeverSkips()
    {
        Assert.False(PlatformFactAttribute.ShouldSkip(TestPlatforms.None, TestPlatforms.Windows));
        Assert.False(PlatformFactAttribute.ShouldSkip(TestPlatforms.None, TestPlatforms.Linux));
        Assert.False(PlatformFactAttribute.ShouldSkip(TestPlatforms.None, TestPlatforms.MacOS));
    }

    [Fact]
    public void ShouldSkip_UnrecognizedCurrentPlatform_RunsRatherThanSkippingEverything()
    {
        // Current answers None on an OS TestPlatforms does not name (FreeBSD, say). Running the
        // test there is the safer default: a gate test that runs somewhere unexpected fails loudly
        // if the gate is wrong, whereas skipping would quietly cover it up.
        var all = TestPlatforms.Windows | TestPlatforms.Linux | TestPlatforms.MacOS;

        Assert.False(PlatformFactAttribute.ShouldSkip(all, TestPlatforms.None));
    }

    #endregion

    #region What the attribute hands xunit

    [Fact]
    public void Skip_NamesTheRunningPlatform_IsSetToTheReason()
    {
        // Derived from Current rather than hard-coded, so this asserts something real on every leg.
        var attribute = new PlatformFactAttribute(PlatformFactAttribute.Current, "because reasons");

        Assert.Equal("because reasons", attribute.Skip);
    }

    [Fact]
    public void Skip_DoesNotNameTheRunningPlatform_IsNullSoTheTestRuns()
    {
        var attribute = new PlatformFactAttribute(NotCurrent(), "because reasons");

        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void SkipOn_IsTheSetItWasGiven()
    {
        var both = TestPlatforms.Windows | TestPlatforms.MacOS;

        Assert.Equal(both, new PlatformFactAttribute(both, "because reasons").SkipOn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ReasonIsMissing_Throws(string? because)
    {
        // The reason becomes the skip message a reader of the results sees, so an empty one would
        // leave them with a skipped test and no explanation.
        // ThrowsAny: the null case surfaces as ArgumentNullException, a subclass.
        Assert.ThrowsAny<ArgumentException>(
            () => new PlatformFactAttribute(TestPlatforms.Windows, because!));
    }

    [Fact]
    public void Current_AgreesWithRuntimeInformation()
    {
        var expected =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TestPlatforms.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? TestPlatforms.Linux
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? TestPlatforms.MacOS
            : TestPlatforms.None;

        Assert.Equal(expected, PlatformFactAttribute.Current);
    }

    #endregion

    #region The three gates it was introduced for

    [Theory]
    [InlineData(
        typeof(Daqifi.Core.Tests.Device.Discovery.WindowsUsbPortDescriptorProviderTests),
        "GetDescriptor_OffWindows_ReturnsNullWithoutTouchingTheMap",
        TestPlatforms.Windows)]
    [InlineData(
        typeof(Daqifi.Core.Tests.Device.Discovery.MacOsUsbPortDescriptorProviderTests),
        "GetDescriptor_OffMacOS_AnswersNullForEveryPort",
        TestPlatforms.MacOS)]
    [InlineData(
        typeof(Daqifi.Core.Tests.Device.Discovery.LinuxUsbPortDescriptorProviderTests),
        "GetDescriptor_OffLinux_AnswersNullForEveryPort",
        TestPlatforms.Linux)]
    public void OffPlatformGateTests_CarryThePlatformFact_SoTheyCannotSilentlyReturnAgain(
        Type testClass,
        string testMethod,
        TestPlatforms expectedSkipOn)
    {
        // Pins the fix: each of these three used to be a [Fact] whose body opened with
        // "if (on my own platform) return;", reporting passed while asserting nothing.
        var method = testClass.GetMethod(testMethod, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var gate = method.GetCustomAttribute<PlatformFactAttribute>();
        Assert.NotNull(gate);
        Assert.Equal(expectedSkipOn, gate.SkipOn);
    }

    #endregion

    /// <summary>
    /// Returns a platform the run is definitely not on, so a test can assert the not-skipped arm
    /// wherever it runs.
    /// </summary>
    private static TestPlatforms NotCurrent()
        => PlatformFactAttribute.Current == TestPlatforms.Windows
            ? TestPlatforms.Linux
            : TestPlatforms.Windows;
}
