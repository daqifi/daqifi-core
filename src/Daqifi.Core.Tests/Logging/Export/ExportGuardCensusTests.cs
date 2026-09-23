using System.Reflection;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;
using Daqifi.Core.Tests.TestSupport;

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
/// <para>
/// The completeness check identifies a throw site by the sentence the guard actually produced,
/// the same way <c>ChannelGuardCensusTests</c> does. A count of <c>SourceFile</c> labels is not
/// enough: a duplicated row can pad a file and hide a throw nobody reaches, and
/// <c>LiveCsvRecording.cs</c> has two <see cref="ArgumentOutOfRangeException"/> sites, so that
/// pad is possible. Reaching a sentence nobody has seen before means reaching a <c>throw</c>
/// nobody has seen before.
/// </para>
/// </remarks>
public class ExportGuardCensusTests
{
    /// <summary>One inline range guard: what the caller sees, and where it lives.</summary>
    /// <param name="Site">Human-readable name, so a failure says which guard drifted.</param>
    /// <param name="SourceFile">
    /// The file the guard is written in. The completeness scan pairs it with the sentence the
    /// guard actually threw, so a duplicated row cannot pad a file's count for a throw no entry
    /// reaches.
    /// </param>
    /// <param name="Method">
    /// The method or constructor that throws. Its parameter list is what
    /// <paramref name="ParamName"/> is resolved against, so a renamed parameter with a stale
    /// <c>nameof</c>-less guard string fails here.
    /// </param>
    /// <param name="ParamName">Expected <see cref="ArgumentException.ParamName"/>, exactly.</param>
    /// <param name="ActualValue">
    /// Expected <see cref="ArgumentOutOfRangeException.ActualValue"/>. Never null: reporting the
    /// rejected value is the whole reason to use the three-argument constructor.
    /// </param>
    /// <param name="Message">
    /// The guard's own sentence, exactly, before the framework's decoration. Also serves as the
    /// throw site's identity: entries that reach the same <c>throw</c> observe the same sentence,
    /// which is what lets the completeness check count throw sites rather than trusting the
    /// <paramref name="SourceFile"/> labels the table hands itself.
    /// </param>
    /// <param name="Act">Invokes the guard with an argument it must reject.</param>
    private sealed record GuardSite(
        string Site,
        string SourceFile,
        MethodBase Method,
        string ParamName,
        object ActualValue,
        string Message,
        Func<Task> Act);

    /// <summary>
    /// Runs every censused guard and reports the file it is declared in alongside the sentence it
    /// actually produced. The sentence is the throw site's identity: two entries that land on one
    /// <c>throw</c> observe the same sentence, and reaching a different sentence means reaching a
    /// different <c>throw</c>.
    /// </summary>
    /// <remarks>
    /// The sentence is declared in the table but is not taken on trust: every entry's message is
    /// pinned to the real exception by <see cref="AssertGuardSentenceIsExactly"/> as it is
    /// observed, so an entry cannot claim a sentence its guard does not produce. That is what
    /// closes the hole a bare file label would leave — a duplicated entry padding the count for a
    /// file and hiding a guard that no entry reaches. <c>LiveCsvRecording.cs</c> has two throw
    /// sites, so that pad is possible. Producing a distinct sentence means actually reaching a
    /// distinct <c>throw</c>.
    /// </remarks>
    private static async Task<IReadOnlyList<(string SourceFile, string Message)>> ObserveGuards()
    {
        var observed = new List<(string SourceFile, string Message)>();

        foreach (var site in Census())
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(site.Act);
            AssertGuardSentenceIsExactly(site, ex);
            observed.Add((site.SourceFile, site.Message));
        }

        return observed;
    }

    /// <summary>
    /// Asserts that the guard produced exactly the sentence the census declares for it — no
    /// prefix match, because the sentence doubles as the throw site's identity and a prefix match
    /// would let one guard's message be a truncation of another's, silently merging two sites.
    /// </summary>
    /// <remarks>
    /// Compared against an exception the framework builds from the census's own declared values,
    /// rather than by pulling the guard's sentence back out of
    /// <see cref="ArgumentOutOfRangeException.Message"/>. Extracting it would mean knowing how the
    /// framework decorates a message with the parameter name and the actual value — and that
    /// decoration comes from a localizable resource, so hardcoding its English form would fail this
    /// census on a machine running a localized runtime, for guards that are perfectly correct.
    /// Building the reference the same way in the same process sidesteps the question entirely:
    /// whatever the decoration is, both sides get it.
    /// </remarks>
    private static void AssertGuardSentenceIsExactly(GuardSite site, ArgumentOutOfRangeException actual)
    {
        var reference = new ArgumentOutOfRangeException(site.ParamName, site.ActualValue, site.Message);

        Assert.Equal(reference.Message, actual.Message);
    }

    /// <summary>
    /// The COMPLETE set of range guards in <c>Logging/Export</c>, not a sample. A guard added to
    /// that folder without an entry here fails
    /// <see cref="ExportRangeGuardCensus_MatchesEveryThrowSiteInTheSource"/>, which reads the
    /// source files rather than trusting this list.
    /// </summary>
    private static GuardSite[] Census()
    {
        var export = SoleMethod(typeof(CsvExporter), nameof(CsvExporter.ExportAsync));
        var record = SoleMethod(
            typeof(LiveCsvRecordingExtensions),
            nameof(LiveCsvRecordingExtensions.RecordLiveSamplesToCsvAsync));
        var sdCardSource = SoleConstructor(typeof(SdCardLogSampleSource));

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

            // A constructor rather than a method, which is why the census resolves a MethodBase:
            // the guard family is about what the caller sees, and that does not change with the
            // kind of member the guard happens to live in.
            new GuardSite(
                $"{nameof(SdCardLogSampleSource)}(analogChannelCount)",
                "SdCardLogSampleSource.cs",
                sdCardSource,
                "analogChannelCount",
                -1,
                "The analog channel count cannot be negative.",
                () =>
                {
                    _ = new SdCardLogSampleSource(NoEntries(), "SN", analogChannelCount: -1);
                    return Task.CompletedTask;
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
        Assert.Equal(4, sites.Length);

        foreach (var site in sites)
        {
            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(site.Act);

            // Compared as one tuple so a failure names the guard that drifted rather than just
            // printing two strings.
            Assert.Equal(
                (site.Site, site.ParamName, (object?)site.ActualValue),
                (site.Site, ex.ParamName, ex.ActualValue));
            AssertGuardSentenceIsExactly(site, ex);

            // The message the caller reads is a sentence about the subject and ends in a period.
            Assert.EndsWith(".", site.Message, StringComparison.Ordinal);
            Assert.Equal(site.Message.Trim(), site.Message);

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
    public async Task ExportRangeGuardCensus_MatchesEveryThrowSiteInTheSource()
    {
        // Read from the source rather than from the census, so a guard added to the folder with no
        // entry in the table turns this red instead of being silently uncovered. Without this the
        // census could only ever check the guards it already knows about.
        //
        // Compared per file as DISTINCT OBSERVED SENTENCES against throw lines. A count of the
        // SourceFile labels the table hands itself is the wrong number: a duplicated entry can pad
        // a file's total and hide a guard that no entry reaches. LiveCsvRecording.cs has two throw
        // sites, so that pad is possible. A distinct sentence has to be earned by actually reaching
        // a distinct throw.
        //
        // Treating the sentence as the identity holds while the throw sites in one file say
        // different things, which they do today. Should two ever collide, this reads one sentence
        // short and fails — the safe direction: it asks for a look rather than passing on a guard
        // nobody exercises. The fix is to give them distinct messages, which a caller wants anyway.
        var found = RangeGuardSourceScanner.ThrowSitesIn(ExportSourceDirectory);

        var reached = (await ObserveGuards()).Distinct().Select(g => g.SourceFile);

        Assert.Equal(
            RangeGuardSourceScanner.SummarizeByFile(found.Select(s => s.File)),
            RangeGuardSourceScanner.SummarizeByFile(reached));
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
        Assert.Contains("SdCardLogSampleSource.cs", files);

        Assert.NotEmpty(RangeGuardSourceScanner.ThrowSitesIn(ExportSourceDirectory));
    }

    /// <summary>
    /// The one public method with this name. Resolved by enumeration rather than
    /// <c>Type.GetMethod(name)</c>, which throws <see cref="System.Reflection.AmbiguousMatchException"/>
    /// the day an overload is added — a failure that says nothing about what to do. This one says
    /// it: an overloaded method needs the census to name which overload it is censusing.
    /// </summary>
    private static MethodInfo SoleMethod(Type type, string name)
    {
        var overloads = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == name)
            .ToList();

        Assert.True(overloads.Count == 1,
            $"Expected exactly one public {type.Name}.{name}; found {overloads.Count}. " +
            "If an overload was added, the census must resolve the specific signature its guard lives in.");

        return overloads[0];
    }

    /// <summary>
    /// The one public constructor of <paramref name="type"/>. Same contract as
    /// <see cref="SoleMethod"/>: an added overload must be named by the census rather than
    /// silently picked for it.
    /// </summary>
    private static ConstructorInfo SoleConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(constructors.Length == 1,
            $"Expected exactly one public {type.Name} constructor; found {constructors.Length}. " +
            "If an overload was added, the census must resolve the specific signature its guard lives in.");

        return constructors[0];
    }

    /// <summary>
    /// An empty sample stream: enough to get past the null check so the range guard below it is
    /// the thing the census reaches.
    /// </summary>
    private static async IAsyncEnumerable<SdCardLogEntry> NoEntries()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static string ExportSourceDirectory =>
        RangeGuardSourceScanner.SourceDirectory("Logging", "Export");

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
