using System.Reflection;
using Daqifi.Core.Channel;
using Daqifi.Core.Tests.TestSupport;

namespace Daqifi.Core.Tests.Channel;

/// <summary>
/// The third guard census, after <c>ScpiMessageProducerTests</c>'s and
/// <c>ExportGuardCensusTests</c>': every inline range guard in <c>Daqifi.Core/Channel</c> is walked
/// here, and what a caller sees when one rejects an argument is pinned and compared against every
/// other one. Issue #664.
/// </summary>
/// <remarks>
/// <para>
/// Channel is the folder a library user meets first — constructing channels, setting calibration
/// coefficients and ranges — so it is the next one worth pinning. It is also where the census has
/// something to say: two of these guards report a null
/// <see cref="ArgumentOutOfRangeException.ActualValue"/> while a third guard with the same message,
/// on the same concept, reports the value. See <see cref="GuardsThatOmitActualValue"/>.
/// </para>
/// <para>
/// A guard here is not the same thing as a public entry point. Five of the twelve throw sites live
/// in shared private validators reached from more than one property or parameter — the
/// <c>MinValue</c> and <c>MaxValue</c> setters both land on <c>RequireFinite</c>, and each passes
/// its own <c>ParamName</c>. The census walks entry points, because <c>ParamName</c> is decided at
/// the call site and that is what the caller reads. The completeness check below still compares
/// like with like: it identifies a throw site by the sentence the guard actually produced, so two
/// entries that land on one <c>throw</c> collapse to one, and reaching a sentence nobody has seen
/// before means reaching a <c>throw</c> nobody has seen before.
/// </para>
/// <para>
/// The existing per-type tests are weaker on this ground on purpose: they assert the exception type
/// and usually <c>ParamName</c>, and none of them notices a guard that stops reporting the offending
/// value or starts quoting a limit it no longer enforces.
/// </para>
/// </remarks>
public class ChannelGuardCensusTests
{
    /// <summary>One caller-visible range guard: what the caller sees, and where it lives.</summary>
    /// <param name="Site">Human-readable name, so a failure says which guard drifted.</param>
    /// <param name="SourceFile">
    /// The file the guard is written in, relative to <c>Daqifi.Core/Channel</c>. Used by the
    /// completeness scan below to check this table against the source rather than against itself.
    /// </param>
    /// <param name="Method">
    /// The member the caller invoked. Its signature is what <paramref name="ParamName"/> is
    /// resolved against, so a renamed parameter or property with a stale guard string fails here.
    /// </param>
    /// <param name="ParamName">Expected <see cref="ArgumentException.ParamName"/>, exactly.</param>
    /// <param name="ActualValue">
    /// Expected <see cref="ArgumentOutOfRangeException.ActualValue"/>, or <c>null</c> for the two
    /// legacy guards listed in <see cref="GuardsThatOmitActualValue"/>.
    /// </param>
    /// <param name="Message">
    /// The guard's own sentence, exactly, before the framework's decoration. Also serves as the
    /// throw site's identity: entries that reach the same <c>throw</c> observe the same sentence,
    /// which is what lets the completeness check below count throw sites rather than entry points
    /// without taking anyone's word for which is which.
    /// </param>
    /// <param name="Act">Invokes the guard with an argument it must reject.</param>
    private sealed record GuardSite(
        string Site,
        string SourceFile,
        MethodBase Method,
        string ParamName,
        object? ActualValue,
        string Message,
        Action Act);

    /// <summary>
    /// The two guards that throw the two-argument <see cref="ArgumentOutOfRangeException"/> and so
    /// hand the caller a null <c>ActualValue</c>.
    /// </summary>
    /// <remarks>
    /// This is drift, not a design decision, and naming it is the point of a census. All three
    /// channel constructors reject a negative channel number with the same sentence, but
    /// <see cref="AnalogOutputChannel"/> — the newest of them — also reports the number that was
    /// rejected, while the two older ones do not. A caller debugging a bad argument gets strictly
    /// less from the older two.
    /// </remarks>
    /// <remarks>
    /// Pinned rather than fixed here: adding <c>ActualValue</c> changes what an existing caller
    /// observes, which is a production change and belongs in its own reviewable commit, not
    /// smuggled in under a test. Listing them exactly is what makes that follow-up findable — and
    /// keeps the list from growing, since a new guard that omits <c>ActualValue</c> is not on it
    /// and fails.
    /// </remarks>
    private static readonly HashSet<string> GuardsThatOmitActualValue = new(StringComparer.Ordinal)
    {
        "AnalogChannel..ctor(channelNumber)",
        "DigitalChannel..ctor(channelNumber)",
    };

    /// <summary>
    /// Runs every censused guard and reports the file it is declared in alongside the sentence it
    /// actually produced. The sentence is the throw site's identity: two entry points that land on
    /// one <c>throw</c> observe the same sentence, and reaching a different sentence means reaching
    /// a different <c>throw</c>.
    /// </summary>
    /// <remarks>
    /// Observed rather than declared, on purpose. An identity the census merely asserts — a label
    /// in the table saying "this entry covers that guard" — is checked by nobody, so a
    /// mislabelled or duplicated entry could pad the count for a file and hide a guard that no
    /// entry reaches. A sentence cannot be padded: producing a new one means actually reaching a
    /// new <c>throw</c>.
    /// </remarks>
    private static IEnumerable<(string SourceFile, string Message)> ObserveGuards() =>
        Census().Select(site =>
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(site.Act);
            return (site.SourceFile, GuardMessage(ex));
        });

    /// <summary>
    /// The sentence the guard itself passed, recovered from the exception the caller receives.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentOutOfRangeException"/> renders its message as the guard's own text, then
    /// <c>" (Parameter 'name')"</c>, then a second line carrying the actual value. Only the first
    /// part is the guard's; the decoration varies per entry point (the <c>MinValue</c> and
    /// <c>MaxValue</c> setters share a <c>throw</c> but name different parameters), so it has to
    /// come off before the sentence can serve as the throw site's identity.
    /// </remarks>
    private static string GuardMessage(ArgumentOutOfRangeException ex)
    {
        var firstLine = ex.Message.Split('\n')[0].TrimEnd('\r');
        var decoration = $" (Parameter '{ex.ParamName}')";

        return firstLine.EndsWith(decoration, StringComparison.Ordinal)
            ? firstLine[..^decoration.Length]
            : firstLine;
    }

    /// <summary>
    /// The COMPLETE set of range guards in <c>Channel</c>, reached through every public entry point
    /// that can trip one — not a sample. A guard added to that folder without an entry here fails
    /// <see cref="ChannelRangeGuardCensus_MatchesEveryThrowSiteInTheSource"/>, which reads the
    /// source files rather than trusting this list.
    /// </summary>
    private static GuardSite[] Census()
    {
        var analogCtor = SoleConstructor(typeof(AnalogChannel));
        var analogOutCtor = SoleConstructor(typeof(AnalogOutputChannel));
        var digitalCtor = SoleConstructor(typeof(DigitalChannel));
        var scalingCtor = SoleConstructor(typeof(ChannelScaling));

        // Built by interpolating the same public constants the guards themselves interpolate,
        // rather than by hardcoding "50" and "1000000". That keeps the census honest if a limit is
        // ever retuned: the message must still state the limit actually enforced. Interpolated the
        // same way the guard does, so the two agree under any culture.
        var resolutionRange =
            $"Resolution must be a plausible ADC max-count between {AnalogChannel.MinResolution} and {AnalogChannel.MaxResolution}.";
        var portRange = $"Port range must be a finite value in (0, {AnalogChannel.MaxPortRangeVolts}] volts.";
        var scaleFactor = $"Scale factor must be a finite, non-zero value within ±{AnalogChannel.MaxCalibrationMagnitude}.";
        var calibrationOffset = $"Calibration offset must be a finite value within ±{AnalogChannel.MaxCalibrationMagnitude}.";
        var dacBits =
            $"DAC resolution must be between {AnalogOutputChannel.MinResolutionBits} and {AnalogOutputChannel.MaxResolutionBits} bits.";
        var rangeEndpoint =
            $"An output range endpoint must be finite and within +/-{AnalogOutputChannel.MaxRangeMagnitudeVolts} V.";

        const string NonNegative = "Channel number must be non-negative.";
        const double TooBig = 1e9;

        return
        [
            // ---- AnalogChannel.cs -------------------------------------------------------------
            new GuardSite(
                "AnalogChannel..ctor(channelNumber)",
                "AnalogChannel.cs",
                analogCtor,
                "channelNumber",
                null,
                NonNegative,
                () => _ = new AnalogChannel(-1)),

            new GuardSite(
                "AnalogChannel..ctor(resolution)",
                "AnalogChannel.cs",
                analogCtor,
                "resolution",
                (uint)0,
                resolutionRange,
                () => _ = new AnalogChannel(0, resolution: 0)),

            // MinValue and MaxValue share one throw site and differ only in the ParamName each
            // passes it. Both are censused: the caller sees two different guards, and a copy-paste
            // slip that made the MaxValue setter report "MinValue" would be invisible to a census
            // that only walked throw sites.
            new GuardSite(
                "AnalogChannel.MinValue",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.MinValue)),
                nameof(AnalogChannel.MinValue),
                double.PositiveInfinity,
                "Value must be a finite number.",
                () => new AnalogChannel(0).MinValue = double.PositiveInfinity),

            new GuardSite(
                "AnalogChannel.MaxValue",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.MaxValue)),
                nameof(AnalogChannel.MaxValue),
                double.PositiveInfinity,
                "Value must be a finite number.",
                () => new AnalogChannel(0).MaxValue = double.PositiveInfinity),

            new GuardSite(
                "AnalogChannel.CalibrationM",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.CalibrationM)),
                nameof(AnalogChannel.CalibrationM),
                0.0,
                scaleFactor,
                () => new AnalogChannel(0).CalibrationM = 0.0),

            new GuardSite(
                "AnalogChannel.InternalScaleM",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.InternalScaleM)),
                nameof(AnalogChannel.InternalScaleM),
                0.0,
                scaleFactor,
                () => new AnalogChannel(0).InternalScaleM = 0.0),

            new GuardSite(
                "AnalogChannel.CalibrationB",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.CalibrationB)),
                nameof(AnalogChannel.CalibrationB),
                TooBig,
                calibrationOffset,
                () => new AnalogChannel(0).CalibrationB = TooBig),

            new GuardSite(
                "AnalogChannel.PortRange",
                "AnalogChannel.cs",
                Setter(typeof(AnalogChannel), nameof(AnalogChannel.PortRange)),
                nameof(AnalogChannel.PortRange),
                0.0,
                portRange,
                () => new AnalogChannel(0).PortRange = 0.0),

            // ---- AnalogOutputChannel.cs -------------------------------------------------------
            new GuardSite(
                "AnalogOutputChannel..ctor(channelNumber)",
                "AnalogOutputChannel.cs",
                analogOutCtor,
                "channelNumber",
                -1,
                NonNegative,
                () => _ = new AnalogOutputChannel(-1)),

            new GuardSite(
                "AnalogOutputChannel..ctor(resolutionBits)",
                "AnalogOutputChannel.cs",
                analogOutCtor,
                "resolutionBits",
                0,
                dacBits,
                () => _ = new AnalogOutputChannel(0, resolutionBits: 0)),

            new GuardSite(
                "AnalogOutputChannel..ctor(minimumVoltage)",
                "AnalogOutputChannel.cs",
                analogOutCtor,
                "minimumVoltage",
                -TooBig,
                rangeEndpoint,
                () => _ = new AnalogOutputChannel(0, minimumVoltage: -TooBig)),

            new GuardSite(
                "AnalogOutputChannel..ctor(maximumVoltage)",
                "AnalogOutputChannel.cs",
                analogOutCtor,
                "maximumVoltage",
                TooBig,
                rangeEndpoint,
                () => _ = new AnalogOutputChannel(0, maximumVoltage: TooBig)),

            // ---- ChannelScaling.cs ------------------------------------------------------------
            new GuardSite(
                "ChannelScaling..ctor(gain)",
                "ChannelScaling.cs",
                scalingCtor,
                "gain",
                double.PositiveInfinity,
                "Gain must be a finite number.",
                () => _ = new ChannelScaling(double.PositiveInfinity)),

            new GuardSite(
                "ChannelScaling..ctor(offset)",
                "ChannelScaling.cs",
                scalingCtor,
                "offset",
                double.PositiveInfinity,
                "Offset must be a finite number.",
                () => _ = new ChannelScaling(1.0, double.PositiveInfinity)),

            // ---- DigitalChannel.cs ------------------------------------------------------------
            new GuardSite(
                "DigitalChannel..ctor(channelNumber)",
                "DigitalChannel.cs",
                digitalCtor,
                "channelNumber",
                null,
                NonNegative,
                () => _ = new DigitalChannel(-1)),
        ];
    }

    [Fact]
    public void EveryRangeGuardInChannel_RejectsOutOfRangeArgumentIdentically()
    {
        var sites = Census();

        // A tripwire, not a proof of completeness — that is the scan below. This fails the moment
        // someone edits the table without reading the note on it, the same mechanism the SCPI and
        // export censuses use.
        Assert.Equal(15, sites.Length);

        foreach (var site in sites)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(site.Act);

            // Compared as one tuple so a failure names the guard that drifted rather than just
            // printing two values.
            Assert.Equal(
                (site.Site, site.ParamName, site.ActualValue),
                (site.Site, ex.ParamName, ex.ActualValue));
            // Exact, not a prefix match: the sentence is also the throw site's identity below, and
            // a prefix match would let one guard's message be a truncation of another's, quietly
            // merging two throw sites into one.
            Assert.Equal(site.Message, GuardMessage(ex));

            // The message the caller reads is a sentence about the subject and ends in a period.
            Assert.EndsWith(".", site.Message, StringComparison.Ordinal);
            Assert.Equal(site.Message.Trim(), site.Message);

            // Reporting the rejected value is the whole reason to use the three-argument
            // constructor, so a null ActualValue is only tolerated for the two guards that already
            // drifted. A new one fails here rather than quietly joining them.
            Assert.True(
                site.ActualValue is not null || GuardsThatOmitActualValue.Contains(site.Site),
                $"{site.Site}: a range guard must report the rejected value as ActualValue. " +
                "Use the three-argument ArgumentOutOfRangeException constructor.");

            AssertParamNameNamesSomethingReal(site);
        }
    }

    /// <summary>
    /// Every <c>ParamName</c> in the census must name something that really exists on the member
    /// the caller invoked: one of its parameters, or — for a property setter, whose only parameter
    /// is the compiler-generated <c>value</c> — the property itself.
    /// </summary>
    /// <remarks>
    /// The setter case is this folder's documented second shape. The BCL convention for a setter is
    /// <c>ParamName == "value"</c>, and these guards name the property instead. That is the more
    /// useful string — <c>"value"</c> tells a caller nothing about which of six coefficients it got
    /// wrong — and it is public contract either way, so it is pinned as it stands rather than
    /// normalised. Checking it structurally is what keeps it from rotting: renaming the property
    /// without updating the <c>nameof</c> in its setter fails here.
    /// </remarks>
    private static void AssertParamNameNamesSomethingReal(GuardSite site)
    {
        if (site.Method is MethodInfo { IsSpecialName: true } method
            && method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            var propertyName = method.Name["set_".Length..];

            Assert.True(propertyName == site.ParamName,
                $"{site.Site}: a setter guard names its property, so ParamName should be " +
                $"'{propertyName}' but is '{site.ParamName}'.");
            Assert.True(
                method.DeclaringType!.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"{site.Site}: ParamName '{site.ParamName}' names no public property on " +
                $"{method.DeclaringType.Name}.");
            return;
        }

        Assert.True(
            Array.Exists(site.Method.GetParameters(), p => p.Name == site.ParamName),
            $"{site.Site}: ParamName '{site.ParamName}' does not name a parameter of " +
            $"{site.Method.DeclaringType!.Name}.{site.Method.Name}.");
    }

    [Fact]
    public void ChannelRangeGuardCensus_MatchesEveryThrowSiteInTheSource()
    {
        // Read from the source rather than from the census, so a guard added to the folder with no
        // entry in the table turns this red instead of being silently uncovered. Without this the
        // census could only ever check the guards it already knows about.
        //
        // Compared per file as DISTINCT OBSERVED SENTENCES against throw lines. Several entries can
        // cover one throw site (the MinValue/MaxValue pair), so entry count is the wrong number —
        // but so is any count of labels the table hands itself, since a mislabelled or duplicated
        // entry could pad a file's total and hide a guard that no entry reaches. A distinct sentence
        // has to be earned by actually reaching a distinct throw.
        //
        // Treating the sentence as the identity holds while the throw sites in one file say
        // different things, which they do today. Should two ever collide, this reads one sentence
        // short and fails — the safe direction: it asks for a look rather than passing on a guard
        // nobody exercises. The fix is to give them distinct messages, which a caller wants anyway.
        var found = RangeGuardSourceScanner.ThrowSitesIn(ChannelSourceDirectory);

        var reached = ObserveGuards().Distinct().Select(g => g.SourceFile);

        Assert.Equal(
            RangeGuardSourceScanner.SummarizeByFile(found.Select(s => s.File)),
            RangeGuardSourceScanner.SummarizeByFile(reached));
    }

    [Fact]
    public void ChannelSourceScan_ActuallyFindsTheSource()
    {
        // Guards the scan above against going vacuous: if the folder moves or the file filter stops
        // matching, MatchesEveryThrowSiteInTheSource would pass by finding nothing on both sides
        // rather than by the guards being censused.
        Assert.True(Directory.Exists(ChannelSourceDirectory),
            $"Expected the channel source at {ChannelSourceDirectory}.");

        var files = Directory
            .GetFiles(ChannelSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();

        Assert.Contains("AnalogChannel.cs", files);
        Assert.Contains("AnalogOutputChannel.cs", files);
        Assert.Contains("ChannelScaling.cs", files);
        Assert.Contains("DigitalChannel.cs", files);

        Assert.NotEmpty(RangeGuardSourceScanner.ThrowSitesIn(ChannelSourceDirectory));
    }

    private static string ChannelSourceDirectory => RangeGuardSourceScanner.SourceDirectory("Channel");

    /// <summary>
    /// The one public constructor of <paramref name="type"/>. Resolved by enumeration rather than
    /// letting reflection pick, so an added overload is a failure that says what to do — the census
    /// must name the signature its guard lives in — instead of a silent wrong choice.
    /// </summary>
    private static ConstructorInfo SoleConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(constructors.Length == 1,
            $"Expected exactly one public {type.Name} constructor; found {constructors.Length}. " +
            "If an overload was added, the census must resolve the specific signature its guard lives in.");

        return constructors[0];
    }

    /// <summary>The public setter of <paramref name="name"/> on <paramref name="type"/>.</summary>
    private static MethodInfo Setter(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(property is not null, $"{type.Name} has no public property '{name}'.");

        var setter = property!.GetSetMethod();
        Assert.True(setter is not null, $"{type.Name}.{name} has no public setter.");

        return setter!;
    }
}
