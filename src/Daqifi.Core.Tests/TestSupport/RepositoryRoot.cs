using System.Reflection;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// The repository this test assembly was built from.
/// </summary>
/// <remarks>
/// <para>
/// <c>Daqifi.Core.Tests.csproj</c> stamps a <c>RepositoryRoot</c> assembly-metadata attribute so a
/// test can read repo files without walking up from the output directory.
/// <c>DirectoryBuildPropsTests</c> set that precedent. The same private property was then copied
/// into the other build guards and into <see cref="RangeGuardSourceScanner"/>; they all read this.
/// </para>
/// <para>
/// A missing attribute fails the way those copies failed.
/// <see cref="Enumerable.Single{TSource}(IEnumerable{TSource}, Func{TSource, bool})"/> throws
/// <see cref="InvalidOperationException"/> ("Sequence contains no matching element"), and this
/// getter does not catch it. Caching the path in a field would wrap that in
/// <see cref="TypeInitializationException"/> on later reads, which is a different failure.
/// </para>
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
