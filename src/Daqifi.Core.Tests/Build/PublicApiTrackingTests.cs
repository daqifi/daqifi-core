using System.Reflection;
using System.Xml.Linq;
using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the public API surface tracking added for issue #636: the
/// <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> wiring in <c>Daqifi.Core.csproj</c>, the
/// two <c>PublicAPI.*.txt</c> files it reads, and the package validation that checks the same
/// surface against the release already on nuget.org.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer is the real guard - RS0016/RS0017 fail the build when the public surface and the
/// API files disagree. What the analyzer cannot guard is its own wiring. Delete the two
/// <c>AdditionalFiles</c> lines and the analyzer finds no API files, reports nothing, and the
/// build stays green with the whole promise of ADR 0002 silently unenforced. That failure mode -
/// a guard that stops guarding without anything going red - is what these tests exist for.
/// </para>
/// <para>
/// The last two tests check the files against the assembly by reflection rather than by reading
/// the csproj, so they still fail if the analyzer is disabled some other way - a severity
/// override, a NoWarn. Their reach is deliberately shallow: <b>public type names only, and only
/// in the direction "declared in the assembly but missing from the files"</b>. A changed
/// signature, a changed nullability annotation, or a removed public member is invisible to them.
/// Catching those is the analyzer's job, and reimplementing its entry format here would just be a
/// second, worse copy of it. What these two do catch is the coarse failure - enforcement off and
/// the files no longer tracking the assembly at all.
/// </para>
/// <para>
/// Every test here was checked to fail on a <b>green</b> build. Two obvious candidates are
/// deliberately absent for failing that check: an entry present in both API files is already
/// RS0025, and dropping <c>PrivateAssets</c> from the analyzer reference already breaks the build
/// because the analyzer then flows down the project reference into this test project. Asserting
/// either would only restate a build error.
/// </para>
/// </remarks>
public class PublicApiTrackingTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(PublicApiTrackingTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string CoreProjectPath =>
        Path.Combine(RepositoryRoot, "src", "Daqifi.Core", "Daqifi.Core.csproj");

    private static string ApiFilePath(string fileName) =>
        Path.Combine(RepositoryRoot, "src", "Daqifi.Core", fileName);

    private const string ShippedFileName = "PublicAPI.Shipped.txt";
    private const string UnshippedFileName = "PublicAPI.Unshipped.txt";

    /// <summary>
    /// The entries of one API file: every non-empty line that is not a directive such as
    /// <c>#nullable enable</c>.
    /// </summary>
    private static IReadOnlyList<string> EntriesIn(string fileName) =>
        File.ReadAllLines(ApiFilePath(fileName))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .ToList();

    [Fact]
    public void CoreProject_ReferencesThePublicApiAnalyzer()
    {
        var reference = XDocument.Load(CoreProjectPath)
            .Descendants("PackageReference")
            .SingleOrDefault(e =>
                string.Equals(
                    (string?)e.Attribute("Include"),
                    "Microsoft.CodeAnalysis.PublicApiAnalyzers",
                    StringComparison.OrdinalIgnoreCase));

        Assert.True(reference != null,
            "Daqifi.Core.csproj no longer references Microsoft.CodeAnalysis.PublicApiAnalyzers, "
            + "so nothing enforces the source-compatibility promise in ADR 0002.");
    }

    [Theory]
    [InlineData(ShippedFileName)]
    [InlineData(UnshippedFileName)]
    public void CoreProject_DeclaresTheApiFileAsAnAdditionalFile(string fileName)
    {
        // This is the silent failure mode: with no AdditionalFiles the analyzer has no API files
        // to compare against, reports nothing at all, and the build stays green.
        var additionalFiles = XDocument.Load(CoreProjectPath)
            .Descendants("AdditionalFiles")
            .Select(e => (string?)e.Attribute("Include"))
            .ToList();

        Assert.Contains(fileName, additionalFiles);
    }

    /// <summary>
    /// The second half of the issue #636 guard: ApiCompat against the package actually on
    /// nuget.org. RS0016/RS0017 only compare the code to <c>PublicAPI.*.txt</c>, and both live in
    /// the PR - remove a public member and its entry together and the build is green. Package
    /// validation compares against a published artifact no PR can edit.
    /// </summary>
    [Fact]
    public void CoreProject_ValidatesThePackageAgainstItsBaseline()
    {
        var value = XDocument.Load(CoreProjectPath)
            .Descendants("EnablePackageValidation")
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

        Assert.Equal("true", value);
    }

    [Fact]
    public void CoreProject_PinsThePackageValidationBaselineToAPublishedVersion()
    {
        var baseline = XDocument.Load(CoreProjectPath)
            .Descendants("PackageValidationBaselineVersion")
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

        // Not a specific version - that has to move with every release. Only that one is pinned:
        // with EnablePackageValidation on but no baseline, validation still runs and still
        // passes, checking the package against nothing but itself.
        Assert.True(
            baseline is not null && Version.TryParse(baseline, out _),
            "Daqifi.Core.csproj must pin PackageValidationBaselineVersion to a published version; "
            + $"found {baseline ?? "<none>"}.");
    }

    [Fact]
    public void Ci_PacksTheLibrarySoPackageValidationActuallyRuns()
    {
        // The trap this exists for: `dotnet pack --no-build` skips the ApiCompat targets
        // outright. A pack step carrying that flag succeeds without comparing anything, so the
        // csproj wiring above would still be present and still be checking nothing. Verified by
        // running both forms locally against a removed public member: without --no-build it is
        // CP0002 on both target frameworks, with it the pack is silent.
        var workflow = File.ReadAllLines(
            Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));

        var packSteps = workflow
            .Select(line => line.Trim())
            .Where(line =>
                line.StartsWith("run:", StringComparison.Ordinal)
                && line.Contains("dotnet pack", StringComparison.Ordinal)
                && line.Contains("Daqifi.Core.csproj", StringComparison.Ordinal))
            .ToList();

        var packStep = Assert.Single(packSteps);
        Assert.DoesNotContain("--no-build", packStep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ShippedFileName)]
    [InlineData(UnshippedFileName)]
    public void ApiFile_ExistsAndEnablesNullability(string fileName)
    {
        var path = ApiFilePath(fileName);
        Assert.True(File.Exists(path), $"Expected {fileName} next to Daqifi.Core.csproj.");

        // Without the directive the analyzer strips nullability from every entry, so a public
        // member changing from `string!` to `string?` would not show up as an API diff at all.
        Assert.Equal("#nullable enable", File.ReadAllLines(path).First().Trim());
    }

    [Fact]
    public void EveryPublicTypeInTheAssembly_IsDeclaredInAnApiFile()
    {
        // Reads the compiled assembly, not the csproj, so it still fails when the analyzer is
        // switched off some other way (a NoWarn, a severity override) and a new public type then
        // never reaches the files.
        //
        // Type names only, and only in this one direction. A removed type, a changed signature or
        // a changed nullability annotation all leave this test green - the analyzer is what
        // catches those, and spelling out entry formats here to catch them too would be a second
        // copy of the analyzer that could disagree with the first.
        var declared = EntriesIn(ShippedFileName)
            .Concat(EntriesIn(UnshippedFileName))
            .ToHashSet(StringComparer.Ordinal);

        var missing = typeof(DaqifiDevice).Assembly
            .GetExportedTypes()
            .Select(ApiNameOf)
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These public types are not declared in either PublicAPI file:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryPublicTypeInTheAssembly_IsFoundByTheNamingUsedAbove()
    {
        // Anti-vacuity guard for the test above: if ApiNameOf ever stopped producing the spelling
        // the API files use, every type would silently "match nothing" - except the assertion is
        // written as "nothing is missing", so the test would still pass while checking nothing.
        // Reverse the question: at least the well-known types must be found, including the two
        // shapes ApiNameOf handles specially.
        var declared = EntriesIn(ShippedFileName)
            .Concat(EntriesIn(UnshippedFileName))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Daqifi.Core.Device.DaqifiDevice", declared);
        // Generic: mangled as `IMessageConsumer\`1` by reflection.
        Assert.Contains("Daqifi.Core.Communication.Consumers.IMessageConsumer<T>", declared);
        // Global namespace: the protoc-generated message type has no namespace at all.
        Assert.Contains("DaqifiOutMessage", declared);
    }

    /// <summary>
    /// The name a type is spelled with in a <c>PublicAPI.*.txt</c> file: namespace-qualified,
    /// nested types joined with a dot rather than reflection's <c>+</c>, and generic type
    /// definitions written with their type parameter names rather than reflection's arity tick.
    /// </summary>
    private static string ApiNameOf(Type type)
    {
        var name = type.Name;

        if (type.IsGenericTypeDefinition)
        {
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                name = name[..tick];
            }

            name += "<" + string.Join(", ", type.GetGenericArguments().Select(a => a.Name)) + ">";
        }

        if (type.IsNested)
        {
            return ApiNameOf(type.DeclaringType!) + "." + name;
        }

        return string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name;
    }
}
