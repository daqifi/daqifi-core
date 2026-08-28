using System.Runtime.InteropServices;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// The operating systems a test can be gated on. A flags enum so one gate can name more than
/// one platform, and so "which platform am I on" and "which platforms does this test skip on"
/// are the same type.
/// </summary>
[Flags]
public enum TestPlatforms
{
    /// <summary>No platform. Also what <see cref="PlatformFactAttribute.Current"/> answers on an
    /// operating system this enum does not name.</summary>
    None = 0,

    /// <summary>Windows.</summary>
    Windows = 1,

    /// <summary>Linux.</summary>
    Linux = 2,

    /// <summary>macOS.</summary>
    MacOS = 4,
}

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test as <em>skipped</em> on the platforms it
/// names, rather than letting it run and assert nothing.
/// </summary>
/// <remarks>
/// <para>
/// Some tests exist to pin a provider's off-platform gate — the branch that has to answer without
/// touching a registry, an <c>ioreg</c> process, or <c>/sys/class/tty</c>. Such a test is
/// meaningful on two of the three CI legs and meaningless on the third. Handling the third with a
/// bare <c>return</c> makes it report <em>passed</em> while asserting nothing, so the run's skip
/// count says nothing about it (issue #663).
/// </para>
/// <para>
/// xunit 2.9.3 has no dynamic skip: <c>Xunit.Sdk.SkipException</c> and the
/// <c>$XunitDynamicSkip$</c> token are present in the assembly, but v2's execution engine does not
/// honour them and turns the throw into a failure. What v2 does honour is
/// <see cref="FactAttribute.Skip"/>, which is read off the attribute instance at discovery time —
/// so the platform decision is made in this constructor instead. Discovery and execution happen in
/// the same process on the same machine, so deciding at discovery is sound.
/// </para>
/// <para>
/// The attribute skips; it never inverts. A test that should run <em>only</em> on one platform is
/// written by naming the other two.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformFactAttribute"/> class.
    /// </summary>
    /// <param name="skipOn">The platforms on which the test is to be skipped.</param>
    /// <param name="because">
    /// Why the test cannot be observed there. Required, and surfaced as the skip reason, so a
    /// reader of the test results learns why the test did not run.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="because"/> is null, empty, or white space.
    /// </exception>
    public PlatformFactAttribute(TestPlatforms skipOn, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        SkipOn = skipOn;

        if (ShouldSkip(skipOn, Current))
        {
            Skip = because;
        }
    }

    /// <summary>Gets the platforms on which this test is skipped.</summary>
    public TestPlatforms SkipOn { get; }

    /// <summary>
    /// Gets the platform this test run is executing on, or <see cref="TestPlatforms.None"/> on an
    /// operating system <see cref="TestPlatforms"/> does not name.
    /// </summary>
    public static TestPlatforms Current { get; } = DetectCurrent();

    /// <summary>
    /// Decides whether a gate naming <paramref name="skipOn"/> skips on <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Split out from the constructor so the decision can be tested against every platform, not
    /// just the one the suite happens to be running on.
    /// </remarks>
    internal static bool ShouldSkip(TestPlatforms skipOn, TestPlatforms current)
        => current != TestPlatforms.None && (skipOn & current) != TestPlatforms.None;

    private static TestPlatforms DetectCurrent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TestPlatforms.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return TestPlatforms.Linux;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TestPlatforms.MacOS;
        }

        return TestPlatforms.None;
    }
}
