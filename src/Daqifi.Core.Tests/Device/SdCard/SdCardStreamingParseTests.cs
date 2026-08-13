using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Pins the lazy-read contract the three SD card parsers promise: a session hands back samples
/// as the file is read, rather than decoding the whole file before the first one appears.
/// </summary>
public class SdCardStreamingParseTests
{
    /// <summary>
    /// A file big enough that reading all of it is obvious in the byte counter, while still
    /// building fast enough for a unit test.
    /// </summary>
    private const int LargeFileMessageCount = 20_000;

    #region Protobuf

    [Fact]
    public async Task ParseAsync_LargeBinFile_FirstSampleDoesNotReadWholeFile()
    {
        var builder = new SdCardTestFileBuilder();
        builder.AddMessage(SdCardTestFileBuilder.CreateStatusMessage());
        for (var i = 0; i < LargeFileMessageCount; i++)
        {
            builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: (uint)((i + 1) * 1000),
                analogFloatValues: new[] { (float)i, i + 0.5f, i + 1.5f, i + 2.5f }));
        }

        using var inner = builder.Build();
        await using var counting = new CountingStream(inner);
        var options = new SdCardParseOptions { BufferSize = 4096 };

        var session = await new SdCardFileParser().ParseAsync(counting, "big.bin", options);

        var enumerator = session.Samples.GetAsyncEnumerator();
        await using (enumerator.ConfigureAwait(false))
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        // The configuration look-ahead plus one read buffer — nowhere near the whole file.
        Assert.True(
            counting.BytesRead < counting.Length / 4,
            $"Read {counting.BytesRead} of {counting.Length} bytes to produce one sample.");
    }

    [Fact]
    public async Task ParseAsync_LargeBinFile_StreamsEverySampleWithoutHoldingTheFile()
    {
        var builder = new SdCardTestFileBuilder();
        builder.AddMessage(SdCardTestFileBuilder.CreateStatusMessage());
        for (var i = 0; i < LargeFileMessageCount; i++)
        {
            builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: (uint)((i + 1) * 1000),
                analogFloatValues: new[] { (float)i }));
        }

        using var stream = builder.Build();
        var session = await new SdCardFileParser().ParseAsync(stream, "big.bin");

        var count = 0;
        var lastValue = double.NaN;
        await foreach (var sample in session.Samples)
        {
            count++;
            lastValue = sample.AnalogValues[0];
        }

        Assert.Equal(LargeFileMessageCount, count);
        Assert.Equal(LargeFileMessageCount - 1, lastValue);
    }

    [Fact]
    public async Task ParseAsync_BinSamples_CanBeEnumeratedMoreThanOnce()
    {
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }));

        using var stream = builder.Build();
        var session = await new SdCardFileParser().ParseAsync(stream, "twice.bin");

        var first = await ToListAsync(session.Samples);
        var second = await ToListAsync(session.Samples);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first.Select(s => (s.Timestamp, s.AnalogValues[0])),
            second.Select(s => (s.Timestamp, s.AnalogValues[0])));
    }

    [Fact]
    public async Task ParseAsync_ForwardOnlyStream_StillParses()
    {
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }));

        using var inner = builder.Build();
        await using var forwardOnly = new ForwardOnlyStream(inner);

        var session = await new SdCardFileParser().ParseAsync(forwardOnly, "forward.bin");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal(1000u, session.DeviceConfig!.TimestampFrequency);
    }

    [Fact]
    public async Task ParseAsync_ConfigurationScanLimit_BoundsHowFarTheConfigScanReads()
    {
        // The serial number only shows up well past the look-ahead window.
        var builder = new SdCardTestFileBuilder();
        for (var i = 0; i < 40; i++)
        {
            builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: (uint)((i + 1) * 1000),
                analogFloatValues: new[] { (float)i }));
        }

        var late = SdCardTestFileBuilder.CreateStreamMessage(41_000, analogFloatValues: new[] { 41f });
        late.DeviceSn = 4242;
        builder.AddMessage(late);

        using var bounded = builder.Build();
        var boundedSession = await new SdCardFileParser().ParseAsync(
            bounded, "late.bin", new SdCardParseOptions { ConfigurationScanMessageLimit = 10 });

        using var unbounded = builder.Build();
        var unboundedSession = await new SdCardFileParser().ParseAsync(
            unbounded, "late.bin", new SdCardParseOptions { ConfigurationScanMessageLimit = 0 });

        Assert.Null(boundedSession.DeviceConfig?.DeviceSerialNumber);
        Assert.Equal("4242", unboundedSession.DeviceConfig?.DeviceSerialNumber);

        // Bounding the config scan must not cost samples.
        Assert.Equal(41, (await ToListAsync(boundedSession.Samples)).Count);
    }

    [Fact]
    public async Task ParseFileAsync_Bin_SamplesReadableAfterTheCallReturns()
    {
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }));

        using var temp = new TempFile(".bin");
        await File.WriteAllBytesAsync(temp.Path, builder.Build().ToArray());

        var session = await new SdCardFileParser().ParseFileAsync(temp.Path);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
    }

    #endregion

    #region CSV

    [Fact]
    public async Task ParseAsync_LargeCsvFile_FirstSampleDoesNotReadWholeFile()
    {
        var rows = Enumerable.Range(0, LargeFileMessageCount)
            .Select(i => ((uint)(i * 100), new[] { (double)i, i + 1.0, i + 2.0, i + 3.0 }))
            .ToArray();

        using var inner = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp("Nq1", "SN1", 1000u, rows);
        await using var counting = new CountingStream(inner);

        var session = await new SdCardCsvFileParser().ParseAsync(counting, "big.csv");

        var enumerator = session.Samples.GetAsyncEnumerator();
        await using (enumerator.ConfigureAwait(false))
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.True(
            counting.BytesRead < counting.Length / 4,
            $"Read {counting.BytesRead} of {counting.Length} bytes to produce one sample.");
    }

    [Fact]
    public async Task ParseAsync_CsvSamples_CanBeEnumeratedMoreThanOnce()
    {
        using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nq1", "SN1", 1000u,
            new[] { (0u, new[] { 1.0 }), (100u, new[] { 2.0 }) });

        var session = await new SdCardCsvFileParser().ParseAsync(stream, "twice.csv");

        var first = await ToListAsync(session.Samples);
        var second = await ToListAsync(session.Samples);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first.Select(s => (s.Timestamp, s.AnalogValues[0])),
            second.Select(s => (s.Timestamp, s.AnalogValues[0])));
    }

    [Fact]
    public async Task ParseAsync_CsvForwardOnlyStream_StillParses()
    {
        using var inner = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nq1", "SN1", 1000u,
            new[] { (0u, new[] { 1.0 }), (100u, new[] { 2.0 }) });
        await using var forwardOnly = new ForwardOnlyStream(inner);

        var session = await new SdCardCsvFileParser().ParseAsync(forwardOnly, "forward.csv");
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
        Assert.Equal("SN1", session.DeviceConfig!.DeviceSerialNumber);
    }

    [Fact]
    public async Task ParseFileAsync_Csv_SamplesReadableAfterTheCallReturns()
    {
        using var built = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nq1", "SN1", 1000u,
            new[] { (0u, new[] { 1.0 }), (100u, new[] { 2.0 }) });

        using var temp = new TempFile(".csv");
        await File.WriteAllBytesAsync(temp.Path, built.ToArray());

        var session = await new SdCardCsvFileParser().ParseFileAsync(temp.Path);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
    }

    #endregion

    #region JSON

    [Fact]
    public async Task ParseAsync_LargeJsonFile_FirstSampleDoesNotReadWholeFile()
    {
        var lines = Enumerable.Range(0, LargeFileMessageCount)
            .Select(i => ((uint)i, new[] { (double)i, i + 1.0, i + 2.0, i + 3.0 }, "01"))
            .ToArray();

        using var inner = SdCardTestJsonFileBuilder.BuildJsonFile(lines);
        await using var counting = new CountingStream(inner);

        var session = await new SdCardJsonFileParser().ParseAsync(
            counting, "big.json", new SdCardParseOptions { FallbackTimestampFrequency = 1000 });

        var enumerator = session.Samples.GetAsyncEnumerator();
        await using (enumerator.ConfigureAwait(false))
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.True(
            counting.BytesRead < counting.Length / 4,
            $"Read {counting.BytesRead} of {counting.Length} bytes to produce one sample.");
    }

    [Fact]
    public async Task ParseAsync_JsonSamples_CanBeEnumeratedMoreThanOnce()
    {
        using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            (0u, new[] { 1.0 }, ""),
            (100u, new[] { 2.0 }, ""));

        var session = await new SdCardJsonFileParser().ParseAsync(
            stream, "twice.json", new SdCardParseOptions { FallbackTimestampFrequency = 1000 });

        var first = await ToListAsync(session.Samples);
        var second = await ToListAsync(session.Samples);

        Assert.Equal(2, first.Count);
        Assert.Equal(
            first.Select(s => (s.Timestamp, s.AnalogValues[0])),
            second.Select(s => (s.Timestamp, s.AnalogValues[0])));
    }

    [Fact]
    public async Task ParseAsync_JsonForwardOnlyStream_StillParses()
    {
        using var inner = SdCardTestJsonFileBuilder.BuildJsonFile(
            (0u, new[] { 1.0 }, ""),
            (100u, new[] { 2.0 }, ""));
        await using var forwardOnly = new ForwardOnlyStream(inner);

        var session = await new SdCardJsonFileParser().ParseAsync(
            forwardOnly, "forward.json", new SdCardParseOptions { FallbackTimestampFrequency = 1000 });
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
    }

    [Fact]
    public async Task ParseFileAsync_Json_SamplesReadableAfterTheCallReturns()
    {
        using var built = SdCardTestJsonFileBuilder.BuildJsonFile(
            (0u, new[] { 1.0 }, ""),
            (100u, new[] { 2.0 }, ""));

        using var temp = new TempFile(".json");
        await File.WriteAllBytesAsync(temp.Path, built.ToArray());

        var session = await new SdCardJsonFileParser().ParseFileAsync(
            temp.Path, new SdCardParseOptions { FallbackTimestampFrequency = 1000 });
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
    }

    #endregion

    #region Shared-stream safety and progress

    [Fact]
    public async Task ParseAsync_StreamBackedSession_RefusesOverlappingEnumerations()
    {
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }));

        using var stream = builder.Build();
        var session = await new SdCardFileParser().ParseAsync(stream, "overlap.bin");

        // One stream, one read position: a second reader while the first is mid-file would
        // silently interleave, so it is refused instead.
        var first = session.Samples.GetAsyncEnumerator();
        await using (first.ConfigureAwait(false))
        {
            Assert.True(await first.MoveNextAsync());

            var second = session.Samples.GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
            await second.DisposeAsync();
        }

        // Once the first enumeration is disposed, the session is readable again.
        Assert.Equal(2, (await ToListAsync(session.Samples)).Count);
    }

    [Fact]
    public async Task ParseFileAsync_FileBackedSession_AllowsOverlappingEnumerations()
    {
        var builder = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
            .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }));

        using var temp = new TempFile(".bin");
        await File.WriteAllBytesAsync(temp.Path, builder.Build().ToArray());

        var session = await new SdCardFileParser().ParseFileAsync(temp.Path);

        // A path-backed session reads the file independently for each enumeration, so two
        // readers cannot interfere.
        var a = session.Samples.GetAsyncEnumerator();
        var b = session.Samples.GetAsyncEnumerator();
        await using (a.ConfigureAwait(false))
        await using (b.ConfigureAwait(false))
        {
            Assert.True(await a.MoveNextAsync());
            Assert.True(await b.MoveNextAsync());
            Assert.Equal(a.Current.AnalogValues[0], b.Current.AnalogValues[0]);
        }
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".json")]
    public async Task ParseAsync_TextProgress_ReachesTheFileLength(string extension)
    {
        var bytes = extension == ".csv"
            ? BuildCsvRows(500)
            : BuildJsonRows(500);

        using var stream = new MemoryStream(bytes);

        SdCardParseProgress? last = null;
        var options = new SdCardParseOptions
        {
            FallbackTimestampFrequency = 1000,
            Progress = new SynchronousProgress<SdCardParseProgress>(p => last = p)
        };

        var session = await SdCardFileParserFactory.ParseAsync(stream, $"log_20240101_120000{extension}", options);
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(500, samples.Count);
        Assert.NotNull(last);
        Assert.Equal(500, last!.MessagesRead);

        // Progress is in bytes and the preamble is part of the file, so a completed read has to
        // land on the file's length rather than stopping short of it.
        Assert.Equal(bytes.Length, last.TotalBytes);
        Assert.Equal(bytes.Length, last.BytesRead);
    }

    private static byte[] BuildCsvRows(int rows)
    {
        using var stream = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nq1", "SN1", 1000u,
            Enumerable.Range(0, rows).Select(i => ((uint)(i * 10), new[] { (double)i })).ToArray());
        return stream.ToArray();
    }

    private static byte[] BuildJsonRows(int rows)
    {
        using var stream = SdCardTestJsonFileBuilder.BuildJsonFile(
            Enumerable.Range(0, rows).Select(i => ((uint)(i * 10), new[] { (double)i }, "")).ToArray());
        return stream.ToArray();
    }

    #endregion

    #region Factory

    [Theory]
    [InlineData(".bin")]
    [InlineData(".csv")]
    [InlineData(".json")]
    public async Task Factory_ParseFileAsync_SamplesReadableAfterTheCallReturns(string extension)
    {
        using var temp = new TempFile(extension);
        await File.WriteAllBytesAsync(temp.Path, BuildTwoSampleFile(extension));

        var session = await SdCardFileParserFactory.ParseFileAsync(
            temp.Path, new SdCardParseOptions { FallbackTimestampFrequency = 1000 });
        var samples = await ToListAsync(session.Samples);

        Assert.Equal(2, samples.Count);
    }

    private static byte[] BuildTwoSampleFile(string extension)
    {
        switch (extension)
        {
            case ".bin":
                using (var bin = new SdCardTestFileBuilder()
                           .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(timestampFreq: 1000))
                           .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(1000, analogFloatValues: new[] { 1.0f }))
                           .AddMessage(SdCardTestFileBuilder.CreateStreamMessage(2000, analogFloatValues: new[] { 2.0f }))
                           .Build())
                {
                    return bin.ToArray();
                }

            case ".csv":
                using (var csv = SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
                           "Nq1", "SN1", 1000u,
                           new[] { (0u, new[] { 1.0 }), (100u, new[] { 2.0 }) }))
                {
                    return csv.ToArray();
                }

            default:
                using (var json = SdCardTestJsonFileBuilder.BuildJsonFile(
                           (0u, new[] { 1.0 }, ""),
                           (100u, new[] { 2.0 }, "")))
                {
                    return json.ToArray();
                }
        }
    }

    #endregion

    #region Helpers

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    /// <summary>
    /// A seekable pass-through that records how many bytes the parser actually pulled.
    /// Byte counting is what makes "does not read the whole file" a deterministic assertion
    /// rather than an allocation measurement at the mercy of the GC.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() => inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that cannot be rewound, standing in for a pipe or socket.
    /// </summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush() => inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string extension)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"daqifi_sd_{Guid.NewGuid():N}{extension}");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }
    }

    #endregion
}
