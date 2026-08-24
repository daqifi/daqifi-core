using System.Reflection;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// The companion to <c>EveryValueRangedProducer_RejectsOutOfRangeArgumentIdentically</c> (in
/// <c>ScpiMessageProducerTests</c>), for a second folder: every inline range guard in
/// <c>Daqifi.Core/Logging/Export</c> is walked here and its observable shape compared against
/// every other one.
/// </summary>
/// <remarks>
/// <para>
/// The SCPI census locks the guard family in one file. Nothing did the same anywhere else, and
/// the guards elsewhere had already drifted apart — some throw the two-argument
/// <see cref="ArgumentOutOfRangeException"/> and so report a null <c>ActualValue</c>, one composes
/// its <c>ParamName</c> instead of using <c>nameof</c>. Export is where the new guards have been
/// landing (#639 added two), so it is the folder worth pinning first. Issue #664 has the full
/// survey; this test deliberately covers that one folder rather than the whole repository.
/// </para>
/// <para>
/// The existing per-site tests are weaker than this on purpose-built ground:
/// <c>LiveCsvRecordingTests</c> asserts the exception type and <c>ParamName</c> only, and
/// <c>CsvExporterTests</c> asserts the type alone. None of them notices if a guard stops
/// reporting the offending value, or starts naming a parameter that no longer exists.
/// </para>
/// </remarks>
public class ExportGuardCensusTests
{
    /// <summary>One inline range guard: what the caller sees, and where it lives.</summary>
    /// <param name="Site">Human-readable name, so a failure says which guard drifted.</param>
    /// <param name="SourceFile">
    /// The file the guard is written in. Used by the completeness scan below to check this table
    /// against the source rather than against itself.
    /// </param>
    /// <param name="Method">
    /// The method that throws. Its parameter list is what <paramref name="ParamName"/> is resolved
    /// against, so a renamed parameter with a stale <c>nameof</c>-less guard string fails here.
    /// </param>
    /// <param name="ParamName">Expected <see cref="ArgumentException.ParamName"/>, exactly.</param>
    /// <param name="ActualValue">
    /// Expected <see cref="ArgumentOutOfRangeException.ActualValue"/>. Never null: reporting the
    /// rejected value is the whole reason to use the three-argument constructor.
    /// </param>
    /// <param name="MessagePrefix">The guard's own message, before the framework's decoration.</param>
    /// <param name="Act">Invokes the guard with an argument it must reject.</param>
    private sealed record GuardSite(
        string Site,
        string SourceFile,
        MethodInfo Method,
        string ParamName,
        object ActualValue,
        string MessagePrefix,
        Func<Task> Act);

    /// <summary>
    /// The COMPLETE set of range guards in <c>Logging/Export</c>, not a sample. A guard added to
    /// that folder without an entry here fails
    /// <see cref="ExportRangeGuardCensus_MatchesEveryThrowSiteInTheSource"/>, which reads the
    /// source files rather than trusting this list.
    /// </summary>
    private static GuardSite[] Census()
    {
        var export = typeof(CsvExporter).GetMethod(nameof(CsvExporter.ExportAsync))!;
        var record = typeof(LiveCsvRecordingExtensions)
            .GetMethod(nameof(LiveCsvRecordingExtensions.RecordLiveSamplesToCsvAsync))!;

        return
        [
            // ParamName here is a composed path, not a bare nameof, and that is deliberate: the
            // caller's parameter is `options`, so `nameof(options)` alone would not say WHICH
            // option was rejected, and the member name alone would not match any parameter the
            // caller passed. It is also part of the public contract — a caller filtering on
            // ParamName sees a change — so it is pinned as-is rather than normalised. The
            // structural check below keeps it honest: `options` must still be a real parameter
            // and `AverageWindow` a real member of its type.
            new GuardSite(
                $"{nameof(CsvExporter)}.{nameof(CsvExporter.ExportAsync)}(options.AverageWindow)",
                "CsvExporter.cs",
                export,
                "options.AverageWindow",
                0,
                "AverageWindow must be greater than zero.",
                () => new CsvExporter().ExportAsync(
                    new InMemorySampleSource([], []),
                    new StringWriter(),
                    new CsvExportOptions { AverageWindow = 0 })),

            new GuardSite(
                $"{nameof(LiveCsvRecordingExtensions.RecordLiveSamplesToCsvAsync)}(duration)",
                "LiveCsvRecording.cs",
                record,
                "duration",
                TimeSpan.Zero,
                "Recording duration must be greater than zero.",
                async () =>
                {
                    using var device = new GuardOnlyLiveDevice();
                    await device.RecordLiveSamplesToCsvAsync(new StringWriter(), duration: TimeSpan.Zero);
                }),

            new GuardSite(
                $"{nameof(LiveCsvRecordingExtensions.RecordLiveSamplesToCsvAsync)}(bufferCapacity)",
                "LiveCsvRecording.cs",
                record,
                "bufferCapacity",
                0,
                "Buffer capacity must be at least 1.",
                async () =>
                {
                    using var device = new GuardOnlyLiveDevice();
                    await device.RecordLiveSamplesToCsvAsync(new StringWriter(), bufferCapacity: 0);
                }),
        ];
    }

    [Fact]
    public async Task EveryRangeGuardInExport_RejectsOutOfRangeArgumentIdentically()
    {
        var sites = Census();

        // A tripwire, not a proof of completeness — that is the scan below. This fails the moment
        // someone edits the table without reading the note on it, the same mechanism the SCPI
        // census uses.
        Assert.Equal(3, sites.Length);

        foreach (var site in sites)
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(site.Act);

            // Compared as one tuple so a failure names the guard that drifted rather than just
            // printing two strings.
            Assert.Equal(
                (site.Site, site.ParamName, (object?)site.ActualValue),
                (site.Site, ex.ParamName, ex.ActualValue));
            Assert.StartsWith(site.MessagePrefix, ex.Message, StringComparison.Ordinal);

            // The message the caller reads is a sentence about the subject and ends in a period.
            // Asserted on the prefix, which the line above has just pinned to the real message.
            Assert.EndsWith(".", site.MessagePrefix, StringComparison.Ordinal);
            Assert.Equal(site.MessagePrefix.Trim(), site.MessagePrefix);

            AssertParamNameNamesSomethingReal(site);
        }
    }

    /// <summary>
    /// Every <c>ParamName</c> in the census must resolve against the throwing method: the first
    /// dot-separated segment is one of its parameters, and any remaining segment is a public
    /// member of that parameter's type. This is what makes the composed
    /// <c>"options.AverageWindow"</c> a documented second shape rather than an unchecked one — it
    /// has to keep pointing at a real parameter and a real property, and a rename that leaves the
    /// string behind fails here.
    /// </summary>
    private static void AssertParamNameNamesSomethingReal(GuardSite site)
    {
        var segments = site.ParamName.Split('.');
        Assert.InRange(segments.Length, 1, 2);

        var parameter = Array.Find(site.Method.GetParameters(), p => p.Name == segments[0]);
        Assert.True(parameter is not null,
            $"{site.Site}: ParamName '{site.ParamName}' does not name a parameter of " +
            $"{site.Method.DeclaringType!.Name}.{site.Method.Name}.");

        if (segments.Length == 1)
        {
            return;
        }

        var owner = Nullable.GetUnderlyingType(parameter!.ParameterType) ?? parameter.ParameterType;
        var member = owner.GetMember(segments[1], BindingFlags.Public | BindingFlags.Instance);
        Assert.True(member.Length > 0,
            $"{site.Site}: ParamName '{site.ParamName}' names no public member " +
            $"'{segments[1]}' on {owner.Name}.");
    }

    [Fact]
    public void ExportRangeGuardCensus_MatchesEveryThrowSiteInTheSource()
    {
        // Read from the source rather than from the census, so a guard added to the folder with no
        // entry in the table turns this red instead of being silently uncovered. Without this the
        // census could only ever check the guards it already knows about.
        var found = ThrowSitesInExportSource();

        var expected = Census()
            .GroupBy(s => s.SourceFile, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var actual = found
            .GroupBy(s => s.File, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            string.Join("; ", expected),
            string.Join("; ", actual));
    }

    [Fact]
    public void ExportSourceScan_ActuallyFindsTheSource()
    {
        // Guards the scan above against going vacuous: if the folder moves or the file filter
        // stops matching, MatchesEveryThrowSiteInTheSource would pass by finding nothing on both
        // sides rather than by the guards being censused.
        Assert.True(Directory.Exists(ExportSourceDirectory),
            $"Expected the export source at {ExportSourceDirectory}.");

        var files = Directory.GetFiles(ExportSourceDirectory, "*.cs").Select(Path.GetFileName).ToList();
        Assert.Contains("CsvExporter.cs", files);
        Assert.Contains("LiveCsvRecording.cs", files);

        Assert.NotEmpty(ThrowSitesInExportSource());
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(ExportGuardCensusTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string ExportSourceDirectory =>
        Path.Combine(RepositoryRoot, "src", "Daqifi.Core", "Logging", "Export");

    /// <summary>
    /// Every line in the export source that mentions <see cref="ArgumentOutOfRangeException"/>
    /// outside a doc comment — the <c>throw new</c> form and the
    /// <c>ArgumentOutOfRangeException.ThrowIf*</c> helpers alike, so switching to a helper does not
    /// slip a guard past the census.
    /// </summary>
    private static IReadOnlyList<(string File, int Line)> ThrowSitesInExportSource()
    {
        var sites = new List<(string File, int Line)>();

        foreach (var path in Directory.GetFiles(ExportSourceDirectory, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (trimmed.Contains(nameof(ArgumentOutOfRangeException), StringComparison.Ordinal))
                {
                    sites.Add((Path.GetFileName(path), i + 1));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// A live-sample device that exists only to get past the null and capability checks so the
    /// range guards can be reached. Its stream throws: the census must never reach it.
    /// </summary>
    private sealed class GuardOnlyLiveDevice : DaqifiStreamingDevice, ILiveSampleSource
    {
        public GuardOnlyLiveDevice() : base("guard-census") { }

        public override void Send<T>(IOutboundMessage<T> message) { /* no transport in tests */ }

        long ILiveSampleSource.DroppedLiveSampleCount => 0;

        IAsyncEnumerable<LiveSample> ILiveSampleSource.StreamSamplesAsync(
            CancellationToken cancellationToken,
            int? bufferCapacity) =>
            throw new InvalidOperationException(
                "The census only exercises argument guards; the stream must never be started.");
    }
}
