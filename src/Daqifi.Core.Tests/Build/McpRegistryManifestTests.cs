using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the MCP Registry listing added for issue #726: the manifest at
/// <c>src/Daqifi.Mcp/.mcp/server.json</c>, the ownership token in the README that ships inside
/// the <c>Daqifi.Mcp</c> NuGet package, and the <c>.github/workflows/release.yml</c> steps that
/// publish the two together.
/// </summary>
/// <remarks>
/// <para>
/// The registry does not take anyone's word for who owns <c>Daqifi.Mcp</c>. It fetches the
/// README of the exact package version named in the manifest and looks for
/// <c>mcp-name: &lt;the manifest's name&gt;</c> in it. Manifest and README are therefore two
/// halves of one claim, each perfectly valid on its own and worthless if they disagree - rename
/// the server, or tidy what looks like a stray HTML comment out of the README, and nothing goes
/// red until a release is already half-published to nuget.org and the registry step fails at the
/// end of it.
/// </para>
/// <para>
/// The same goes for the version. The committed manifest names the last released version so it
/// stays readable and valid on its own; what actually ships is stamped in from the tag during
/// the release. Drop that stamping step and every release would re-publish a claim about an old
/// version - which the registry would either reject or, worse, accept as the current listing.
/// </para>
/// <para>
/// The repository root is captured at build time as an assembly-metadata attribute; see
/// <c>Daqifi.Core.Tests.csproj</c>.
/// </para>
/// </remarks>
public class McpRegistryManifestTests
{
    /// <summary>Path of the manifest relative to the repository root, in the form the workflow uses.</summary>
    private const string ManifestRelativePath = "src/Daqifi.Mcp/.mcp/server.json";

    /// <summary>
    /// The only registry base URL the MCP Registry accepts for <c>registryType: nuget</c>; it
    /// compares the value literally and rejects every other spelling of nuget.org.
    /// </summary>
    private const string NuGetRegistryBaseUrl = "https://api.nuget.org/v3/index.json";

    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(McpRegistryManifestTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string PathFromRoot(string relativePath) =>
        Path.Combine(RepositoryRoot, Path.Combine(relativePath.Split('/')));

    private static JsonElement Manifest =>
        JsonDocument.Parse(File.ReadAllText(PathFromRoot(ManifestRelativePath))).RootElement;

    private static JsonElement NuGetPackage =>
        Manifest.GetProperty("packages")
            .EnumerateArray()
            .Single(p => p.GetProperty("registryType").GetString() == "nuget");

    private static XDocument McpProject =>
        XDocument.Load(PathFromRoot("src/Daqifi.Mcp/Daqifi.Mcp.csproj"));

    /// <summary>The single value of an MSBuild property declared in <c>Daqifi.Mcp.csproj</c>.</summary>
    /// <remarks>
    /// Matched case-insensitively because MSBuild property names are: a
    /// <c>&lt;packageid&gt;</c> sets the same property as <c>&lt;PackageId&gt;</c>, and an
    /// ordinal match would read it as absent and pass the comparison vacuously against null.
    /// </remarks>
    private static string? McpProperty(string name) =>
        McpProject
            .Descendants("PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

    /// <summary>The README that <c>Daqifi.Mcp</c> packs, which is the one the registry reads.</summary>
    private static string PackageReadmeText =>
        File.ReadAllText(Path.Combine(
            PathFromRoot("src/Daqifi.Mcp"),
            McpProperty("PackageReadmeFile") ?? throw new InvalidOperationException(
                "Daqifi.Mcp.csproj declares no PackageReadmeFile, so the package ships no README "
                + "for the MCP Registry to read its ownership token from (issue #726).")));

    private static string ReleaseWorkflowText =>
        File.ReadAllText(PathFromRoot(".github/workflows/release.yml"));

    /// <summary>
    /// The server name claimed by the <c>mcp-name:</c> token in the packaged README, or null.
    /// </summary>
    /// <remarks>
    /// The registry requires the token to be followed by a boundary rather than by more name
    /// characters, so that <c>mcp-name: io.github.daqifi/daqifi-mcp-extra</c> cannot be read as a
    /// claim on <c>io.github.daqifi/daqifi-mcp</c>. Matching a whole run of name characters here
    /// reproduces that: a glued suffix is captured as part of the name and fails the comparison
    /// instead of being silently trimmed away.
    /// </remarks>
    private static string? ReadmeMcpName =>
        Regex.Matches(PackageReadmeText, @"mcp-name:[ \t]*(?<name>[^\s<>]+)")
            .Select(m => m.Groups["name"].Value)
            .SingleOrDefault();

    [Fact]
    public void Manifest_DeclaresAServerTheRegistryCanIdentify()
    {
        // Anti-vacuity: everything below reads these, so a manifest that had quietly lost its
        // name or its NuGet package entry would make the rest of the class pass by comparing
        // nothing to nothing.
        Assert.False(string.IsNullOrWhiteSpace(Manifest.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(NuGetPackage.GetProperty("identifier").GetString()));
    }

    [Fact]
    public void Manifest_PinsARegistrySchema()
    {
        // mcp-publisher rejects a manifest with no $schema outright, and rejects a superseded one
        // as a deprecated-schema error, so the release would fail after the NuGet push has
        // already happened. Nothing here can know which schema date is current, but a $schema
        // that has stopped being one of the registry's own is a certain failure.
        var schema = Manifest.GetProperty("$schema").GetString();

        Assert.StartsWith(
            "https://static.modelcontextprotocol.io/schemas/",
            schema,
            StringComparison.Ordinal);
        Assert.EndsWith("/server.schema.json", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ClaimsTheNamespaceGitHubOidcGrantsThisRepository()
    {
        // The release authenticates with GitHub OIDC, which grants exactly
        // `io.github.<repository owner>/*`. A name outside it is rejected as unauthorised no
        // matter how well-formed the rest of the manifest is.
        var repositoryUrl = XDocument.Load(PathFromRoot("Directory.Build.targets"))
            .Descendants("RepositoryUrl")
            .Select(e => e.Value.Trim())
            .Single();
        var owner = new Uri(repositoryUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];

        Assert.StartsWith($"io.github.{owner}/", Manifest.GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedReadme_CarriesTheOwnershipTokenForTheManifestName()
    {
        // The whole ownership proof. Without this line in the README of the published version,
        // the registry refuses the listing and says to publish a new package version - which
        // means waiting for the next release to fix it.
        var name = Manifest.GetProperty("name").GetString();

        Assert.True(ReadmeMcpName == name,
            $"The manifest names the server '{name}' but the README packed into Daqifi.Mcp "
            + $"claims '{ReadmeMcpName ?? "(no mcp-name: token)"}'. The MCP Registry proves "
            + "ownership by finding `mcp-name: <the manifest's name>` in the published package's "
            + "README, so a mismatch fails the release after the package is already on nuget.org "
            + "(issue #726).");
    }

    [Fact]
    public void Manifest_PointsAtTheNuGetPackageThisRepositoryPublishes()
    {
        Assert.Equal(McpProperty("PackageId"), NuGetPackage.GetProperty("identifier").GetString());
    }

    [Fact]
    public void Manifest_UsesTheOnlyNuGetBaseUrlTheRegistryAccepts()
    {
        // Optional in the schema, but the registry compares it literally when it is present:
        // https://api.nuget.org (no path) or a trailing slash is rejected as "not valid for
        // registry type nuget".
        Assert.Equal(NuGetRegistryBaseUrl, NuGetPackage.GetProperty("registryBaseUrl").GetString());
    }

    [Fact]
    public void Manifest_DescribesTheServerAsStdio()
    {
        // Daqifi.Mcp speaks MCP over stdio and is launched as a subprocess; a client that read
        // any other transport from the listing would try to reach it over HTTP and find nothing.
        Assert.Equal("stdio", NuGetPackage.GetProperty("transport").GetProperty("type").GetString());
    }

    [Fact]
    public void Manifest_VersionsTheListingAndThePackageTogether()
    {
        // The release stamps one tag into both, and the registry verifies ownership against the
        // package version specifically, so a listing whose two versions disagree describes a
        // release that never existed.
        Assert.Equal(Manifest.GetProperty("version").GetString(), NuGetPackage.GetProperty("version").GetString());
    }

    [Fact]
    public void Manifest_CarriesAVersionTheReleaseWorkflowWouldAccept()
    {
        // Same shape release.yml enforces on the tag, so the committed placeholder is a version
        // this repository could actually have released rather than a word like "dev".
        Assert.Matches(@"^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.]+)?$", Manifest.GetProperty("version").GetString());
    }

    [Fact]
    public void Manifest_DescriptionFitsTheRegistryLimit()
    {
        // The registry caps it at 100 characters and rejects the publish over that.
        var description = Manifest.GetProperty("description").GetString();

        Assert.NotEmpty(description!);
        Assert.True(description!.Length <= 100,
            $"The manifest description is {description.Length} characters; the MCP Registry "
            + "schema caps it at 100 and rejects the publish.");
    }

    [Fact]
    public void ReleaseWorkflow_PublishesTheManifestThatActuallyExists()
    {
        // The path is repeated in the workflow as a string, so moving or renaming the manifest
        // leaves `mcp-publisher publish` pointing at nothing - which it reports as "server.json
        // not found. Run 'mcp-publisher init' to create one", long after the NuGet push.
        Assert.Contains($"mcp-publisher publish {ManifestRelativePath}", ReleaseWorkflowText, StringComparison.Ordinal);
        Assert.True(File.Exists(PathFromRoot(ManifestRelativePath)));
    }

    [Fact]
    public void ReleaseWorkflow_InstallsAPinnedChecksummedMcpPublisher()
    {
        // The upstream docs' snippet pipes `releases/latest/download` straight into tar, and
        // copying it back would be an easy tidy-up. That binary runs in the job holding
        // `id-token: write` for this repository's registry namespace, so whoever controls the
        // artifact it fetches controls what gets listed as ours.
        Assert.DoesNotContain("releases/latest/download", ReleaseWorkflowText, StringComparison.Ordinal);
        Assert.Matches(@"MCP_PUBLISHER_VERSION:\s*v[0-9]+\.[0-9]+\.[0-9]+", ReleaseWorkflowText);
        Assert.Contains("sha256sum -c", ReleaseWorkflowText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_AuthenticatesWithGitHubOidc()
    {
        // Issue #726 asked for OIDC rather than a long-lived token: no secret to rotate, and the
        // grant is scoped to this repository's own io.github namespace.
        Assert.Contains("mcp-publisher login github-oidc", ReleaseWorkflowText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_RequestsTheTokenOidcNeeds()
    {
        // `login github-oidc` reads the workflow's OIDC token; without id-token: write it is not
        // issued and authentication fails. The NuGet login step needs the same permission, so
        // this is easy to assume is someone else's problem and remove.
        Assert.Contains("id-token: write", ReleaseWorkflowText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_StampsTheTagIntoBothVersionFields()
    {
        var filter = Regex.Match(ReleaseWorkflowText, @"jq\s+--arg\s+v\s+""\$VERSION""\s+'(?<filter>[^']*)'")
            .Groups["filter"].Value;

        Assert.Contains(".version = $v", filter, StringComparison.Ordinal);
        Assert.Contains(".packages[].version = $v", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_ListsTheVersionOnlyAfterPushingIt()
    {
        // The registry fetches the package's README to verify ownership, so the version has to
        // be on nuget.org before the listing is published. Ordering the steps the other way
        // round would fail every release with "package does not exist", and only for the version
        // being released, so a local run of anything would still look fine.
        var push = ReleaseWorkflowText.IndexOf("nuget push nupkgs/Daqifi.Mcp", StringComparison.Ordinal);
        var publish = ReleaseWorkflowText.IndexOf("mcp-publisher publish", StringComparison.Ordinal);

        Assert.True(push >= 0 && publish > push,
            "release.yml must push Daqifi.Mcp to NuGet before publishing the manifest to the MCP "
            + "Registry; the registry verifies ownership by reading the published package's "
            + "README (issue #726).");
    }
}
