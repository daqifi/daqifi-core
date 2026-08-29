using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Sd = Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Pins what every SD card log parser does with a non-positive
/// <see cref="Sd.SdCardParseOptions.BufferSize"/>: it is rejected up front, by all three
/// formats, with the same exception (issue #712).
/// </summary>
/// <remarks>
/// <para>
/// All three parsers are reachable through one front door,
/// <see cref="Sd.SdCardFileParserFactory"/>, which picks a parser from the file extension. They
/// used to disagree about this option. <c>SdCardFileParser</c> (<c>.bin</c>) guarded it; the CSV
/// and JSON parsers passed it straight to <c>FileStream</c>, which has a different contract —
/// it rejects a negative buffer size naming <c>bufferSize</c>, an internal parameter the caller
/// never wrote, and accepts <b>zero</b> outright as "no buffering". So the same caller mistake
/// either threw with a useful name or was silently honoured, decided only by the file extension.
/// </para>
/// <para>
/// Zero is the case that settles the contract, which is why it is covered separately from a
/// negative: <c>FileStream</c> is happy with it, but the protobuf parser allocates
/// <c>new byte[BufferSize]</c>, and a zero-length read buffer can never make progress. The
/// protobuf parser's <c>&lt;= 0</c> is therefore the rule for all three.
/// </para>
/// </remarks>
public sealed class SdCardParseOptionsBufferSizeGuardTests : IDisposable
{
    /// <summary>The guard's own message, before the framework appends its parameter decoration.</summary>
    private const string ExpectedMessagePrefix = "BufferSize must be greater than zero.";

    /// <summary>
    /// Every public parse entry point names its options parameter <c>options</c>, so that is what
    /// the caller sees reported back. Held apart from the guard so a drift shows up here.
    /// </summary>
    private const string ExpectedParamName = "options";

    private readonly string _directory;

    public SdCardParseOptionsBufferSizeGuardTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "daqifi-712-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        // Real, well-formed logs. A parser that reached the file at all would parse these
        // happily, so a test that still throws is throwing over the option, not the file.
        var builder = new SdCardTestFileBuilder();
        builder.AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000));
        builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 1000, analogFloatValues: [1.0f]));
        using (var bin = builder.Build())
        {
            File.WriteAllBytes(Path.Combine(_directory, "log.bin"), bin.ToArray());
        }

        File.WriteAllText(
            Path.Combine(_directory, "log.csv"),
            "# Device: Nyquist 1\n# Timestamp Tick Rate: 1000 Hz\nain0_ts,ain0_val\n1000,1.5\n");

        File.WriteAllText(
            Path.Combine(_directory, "log.json"),
            "{\"ts\":1000,\"analog\":[1.5],\"digital\":\"00\"}\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>
    /// One public parse entry point, and how to call it with options the guard must reject.
    /// </summary>
    /// <param name="DeclaringType">The parser the entry point belongs to.</param>
    /// <param name="MethodName">Which overload — <c>ParseAsync</c> or <c>ParseFileAsync</c>.</param>
    /// <param name="Call">Invokes the entry point against this fixture's log files.</param>
    private sealed record ParseEntryPoint(
        Type DeclaringType,
        string MethodName,
        Func<SdCardParseOptionsBufferSizeGuardTests, Sd.SdCardParseOptions, Task> Call)
    {
        public string Key => $"{DeclaringType.Name}.{MethodName}";
    }

    /// <summary>
    /// The COMPLETE set of public parse entry points across the three parsers — checked against
    /// the library's own surface by
    /// <see cref="TheGuardedEntryPoints_AreEveryPublicParseEntryPointInTheLibrary"/>, so a fourth
    /// format (or a new overload on an existing one) cannot quietly skip the guard.
    /// </summary>
    private static ParseEntryPoint[] EntryPoints() =>
    [
        new(typeof(Sd.SdCardFileParser), nameof(Sd.SdCardFileParser.ParseAsync),
            (t, o) => new Sd.SdCardFileParser().ParseAsync(t.ReadLog("log.bin"), "log.bin", o)),
        new(typeof(Sd.SdCardFileParser), nameof(Sd.SdCardFileParser.ParseFileAsync),
            (t, o) => new Sd.SdCardFileParser().ParseFileAsync(t.PathTo("log.bin"), o)),

        new(typeof(Sd.SdCardCsvFileParser), nameof(Sd.SdCardCsvFileParser.ParseAsync),
            (t, o) => new Sd.SdCardCsvFileParser().ParseAsync(t.ReadLog("log.csv"), "log.csv", o)),
        new(typeof(Sd.SdCardCsvFileParser), nameof(Sd.SdCardCsvFileParser.ParseFileAsync),
            (t, o) => new Sd.SdCardCsvFileParser().ParseFileAsync(t.PathTo("log.csv"), o)),

        new(typeof(Sd.SdCardJsonFileParser), nameof(Sd.SdCardJsonFileParser.ParseAsync),
            (t, o) => new Sd.SdCardJsonFileParser().ParseAsync(t.ReadLog("log.json"), "log.json", o)),
        new(typeof(Sd.SdCardJsonFileParser), nameof(Sd.SdCardJsonFileParser.ParseFileAsync),
            (t, o) => new Sd.SdCardJsonFileParser().ParseFileAsync(t.PathTo("log.json"), o)),
    ];

    /// <summary>
    /// Each entry point is its own test case on purpose: which sites throw and which do not is
    /// the whole finding, and one aggregate assertion would hide it behind the first failure.
    /// </summary>
    [Theory]
    [InlineData("SdCardFileParser.ParseAsync", 0)]
    [InlineData("SdCardFileParser.ParseAsync", -1)]
    [InlineData("SdCardFileParser.ParseFileAsync", 0)]
    [InlineData("SdCardFileParser.ParseFileAsync", -1)]
    [InlineData("SdCardCsvFileParser.ParseAsync", 0)]
    [InlineData("SdCardCsvFileParser.ParseAsync", -1)]
    [InlineData("SdCardCsvFileParser.ParseFileAsync", 0)]
    [InlineData("SdCardCsvFileParser.ParseFileAsync", -1)]
    [InlineData("SdCardJsonFileParser.ParseAsync", 0)]
    [InlineData("SdCardJsonFileParser.ParseAsync", -1)]
    [InlineData("SdCardJsonFileParser.ParseFileAsync", 0)]
    [InlineData("SdCardJsonFileParser.ParseFileAsync", -1)]
    public async Task ParseEntryPoint_WithNonPositiveBufferSize_ThrowsTheSameArgumentOutOfRange(
        string entryPointKey,
        int bufferSize)
    {
        // Arrange
        var entryPoint = EntryPoints().Single(e => e.Key == entryPointKey);
        var options = new Sd.SdCardParseOptions { BufferSize = bufferSize };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => entryPoint.Call(this, options));

        // Assert — the shape a caller can actually branch on, compared as one tuple so a failure
        // names the entry point that drifted rather than printing two bare strings.
        Assert.Equal(
            (entryPointKey, ExpectedParamName),
            (entryPointKey, ex.ParamName));
        Assert.StartsWith(ExpectedMessagePrefix, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard has to fire before the parser touches the filesystem, so a caller learns about a
    /// bad option from the call that set it rather than from something further downstream.
    /// </summary>
    [Theory]
    [InlineData("log.bin")]
    [InlineData("log.csv")]
    [InlineData("log.json")]
    public async Task ParseFileAsync_WithNonPositiveBufferSize_ThrowsBeforeOpeningTheFile(string fileName)
    {
        // Arrange — a path that does not exist. Reaching the filesystem at all would report the
        // missing file instead of the bad option, so the exception type says which check ran first.
        var missing = Path.Combine(_directory, "no-such-directory", fileName);
        var options = new Sd.SdCardParseOptions { BufferSize = 0 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Sd.SdCardFileParserFactory.ParseFileAsync(missing, options));
    }

    /// <summary>
    /// A default-constructed options object is valid, so the guard cannot be firing on the happy
    /// path. Cheap, and it is the assertion that catches an inverted comparison.
    /// </summary>
    [Theory]
    [InlineData("log.bin")]
    [InlineData("log.csv")]
    [InlineData("log.json")]
    public async Task ParseFileAsync_WithDefaultOptions_StillParses(string fileName)
    {
        var session = await Sd.SdCardFileParserFactory.ParseFileAsync(PathTo(fileName));

        Assert.NotNull(session);
        Assert.Equal(fileName, session.FileName);
    }

    /// <summary>
    /// Completeness: the table above must cover every public parse entry point the library
    /// exposes. The expected side is discovered from the assembly rather than restated here, so a
    /// fourth format's parser — or a new overload on an existing one — fails this test until it
    /// has a row of its own and, with it, a guard.
    /// </summary>
    [Fact]
    public void TheGuardedEntryPoints_AreEveryPublicParseEntryPointInTheLibrary()
    {
        var discovered = DiscoverParseEntryPoints();

        // Discovery has to find something before an equality against it means anything: a filter
        // that silently matched nothing would make every assertion below vacuously true.
        Assert.NotEmpty(discovered);

        // The table keys entry points by type-and-method-name, which describes the surface
        // completely only while each name resolves to exactly one method. Without this, a new
        // overload would inherit its sibling's row and never be called with a bad buffer size.
        foreach (var overloads in discovered.GroupBy(EntryPointKey, StringComparer.Ordinal))
        {
            Assert.True(
                overloads.Count() == 1,
                $"{overloads.Key} now has {overloads.Count()} overloads. Give the new one its own " +
                "row in EntryPoints() — and its own guard — rather than letting it share a key.");
        }

        var declared = discovered
            .Select(EntryPointKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var covered = EntryPoints()
            .Select(e => e.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal<IEnumerable<string>>(declared, covered);
    }

    /// <summary>
    /// Discovers — rather than restates — the parse entry points: every public instance method on
    /// <c>Daqifi.Core</c>'s exported surface that accepts an <see cref="Sd.SdCardParseOptions"/>.
    /// Nothing about the three current parsers is named here, so a fourth format's parser turns up
    /// without anyone remembering to edit this file. That is the point: #712 happened because two
    /// formats missed a check nobody had enumerated.
    /// </summary>
    /// <remarks>
    /// <see cref="Sd.SdCardFileParserFactory"/> falls outside this by being static, not by being
    /// named: every one of its methods forwards to one of these, so covering it would assert the
    /// same guard twice.
    /// </remarks>
    private static MethodInfo[] DiscoverParseEntryPoints() =>
        typeof(Sd.SdCardParseOptions).Assembly
            .GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => Array.Exists(m.GetParameters(), p => p.ParameterType == typeof(Sd.SdCardParseOptions)))
            .ToArray();

    private static string EntryPointKey(MethodInfo method) =>
        $"{method.DeclaringType!.Name}.{method.Name}";

    private string PathTo(string fileName) => Path.Combine(_directory, fileName);

    /// <summary>
    /// The log's bytes in memory, so a guard that throws before reading leaves no open handle
    /// for the fixture's temp-directory cleanup to trip over.
    /// </summary>
    private MemoryStream ReadLog(string fileName) => new(File.ReadAllBytes(PathTo(fileName)));
}
