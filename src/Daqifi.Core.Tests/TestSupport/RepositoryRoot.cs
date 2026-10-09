using System.Reflection;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// The repository this test assembly was built from, for tests that read repo files (build
/// guards, source scans).
/// </summary>
/// <remarks>
/// <c>Daqifi.Core.Tests.csproj</c> stamps the path in at build time as a <c>RepositoryRoot</c>
/// assembly-metadata attribute, so it does not depend on where the test binary runs from. The
/// getter is deliberately not cached in a field: a missing attribute then fails every read with
/// the plain <see cref="InvalidOperationException"/> from <c>Single</c>, not a
/// <see cref="TypeInitializationException"/>.
/// </remarks>
internal static class RepositoryRoot
{
    /// <summary>Absolute path of the repository root.</summary>
    internal static string FullPath =>
        Path.GetFullPath(
            typeof(RepositoryRoot).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);
}
