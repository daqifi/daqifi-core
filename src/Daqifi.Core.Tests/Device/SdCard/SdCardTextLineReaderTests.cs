using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct tests for <see cref="SdCardTextLineReader"/>, the line pump behind the CSV and JSON
/// log parsers.
/// </summary>
/// <remarks>
/// <para>
/// Every non-blank line of a downloaded text log reaches a parser through this class, carrying
/// the running byte count that drives the caller's progress reporting. It had no tests of its
/// own: the CSV and JSON parser fixtures feed it small, well-formed, seekable
/// <see cref="MemoryStream"/>s positioned at zero and assert on the samples that come out the
/// far end, so they never observe the byte counter at all and never take the non-seekable
/// branch.
/// </para>
/// <para>
/// That left the parts that decide what a progress bar shows unpinned — that a seekable source
/// reports the stream's real position while a forward-only one reports an estimate, that blank
/// lines are dropped from the output but still counted toward that estimate, and that reading
/// starts where the caller left the stream rather than at byte zero. It also left the lease
/// discipline of the source overload unpinned: that overload is the only thing that returns a
/// stream-backed source's single read cursor, and it has to return it when the consumer stops
/// early as well as when the file runs out.
/// </para>
/// </remarks>
public class SdCardTextLineReaderTests
{
    private const string Content = "alpha\nbravo\ncharlie\n";

    #region Blank-line handling and the byte counter

    [Fact]
    public async Task ReadLines_DropsBlankAndWhitespaceOnlyLines()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("alpha\n\nbravo\n   \n\t\ncharlie\n"));

        var lines = await ReadAllAsync(stream);

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, lines.Select(l => l.Text));
    }

    [Fact]
    public async Task ReadLines_OnAForwardOnlyStream_CountsSkippedBlankLinesTowardTheEstimate()
    {
        // "alpha\n" is 6 bytes and the blank line 1 more, so "bravo" must be reported at 13 —
        // the 12 it would carry if the blank were skipped before the counter ran is wrong.
        using var stream = new ForwardOnlyStream(Encoding.ASCII.GetBytes("alpha\n\nbravo\n"));

        var lines = await ReadAllAsync(stream);

        Assert.Equal(new[] { 6L, 13L }, lines.Select(l => l.BytesRead));
    }

    [Fact]
    public async Task ReadLines_OnAForwardOnlyStream_CountsEachLineTerminator()
    {
        using var stream = new ForwardOnlyStream(Encoding.ASCII.GetBytes(Content));

        var lines = await ReadAllAsync(stream);

        Assert.Equal(new[] { 6L, 12L, 20L }, lines.Select(l => l.BytesRead));
    }

    [Fact]
    public async Task ReadLines_OnAForwardOnlyStream_CountsATerminatorForAnUnterminatedFinalLine()
    {
        // The device does not always close a log with a newline. The estimate is deliberately
        // "as if it did", so the final line reports 20 for a 19-byte payload.
        using var stream = new ForwardOnlyStream(Encoding.ASCII.GetBytes("alpha\nbravo\ncharlie"));

        var lines = await ReadAllAsync(stream);

        Assert.Equal("charlie", lines[^1].Text);
        Assert.Equal(20L, lines[^1].BytesRead);
    }

    [Fact]
    public async Task ReadLines_OnASeekableStream_ReportsTheStreamsRealPositionNotTheEstimate()
    {
        // The whole 20-byte payload fits in one StreamReader buffer, so the stream is already at
        // EOF by the time the first line is handed back. Every line therefore reports 20 — which
        // is exactly what tells this apart from the 6/12/20 estimate.
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));

        var lines = await ReadAllAsync(stream);

        Assert.Equal(new[] { 20L, 20L, 20L }, lines.Select(l => l.BytesRead));
    }

    #endregion

    #region Where reading starts, and what happens to the stream afterwards

    [Fact]
    public async Task ReadLines_StartsAtTheStreamsCurrentPositionNotAtZero()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        stream.Position = 6;

        var lines = await ReadAllAsync(stream);

        Assert.Equal(new[] { "bravo", "charlie" }, lines.Select(l => l.Text));
    }

    [Fact]
    public async Task ReadLines_LeavesTheCallersStreamOpen()
    {
        using var stream = new DisposeCountingStream(Encoding.ASCII.GetBytes(Content));

        await ReadAllAsync(stream);

        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task ReadLines_OnAStreamOfNothingButBlankLines_YieldsNothing()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("\n \n\t\n\r\n"));

        Assert.Empty(await ReadAllAsync(stream));
    }

    [Fact]
    public async Task ReadLines_WithAnAlreadyCancelledToken_Throws()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SdCardTextLineReader.ReadLinesAsync(stream, cts.Token))
            {
            }
        });
    }

    #endregion

    #region The source overload's lease discipline

    [Fact]
    public async Task ReadLines_OverASource_ReleasesTheLeaseWhenTheFileRunsOut()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        await ReadAllAsync(source);

        // A leaked latch would make this throw "already being read".
        var second = await ReadAllAsync(source);
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, second.Select(l => l.Text));
    }

    [Fact]
    public async Task ReadLines_OverASource_ReleasesTheLeaseWhenTheConsumerStopsEarly()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        await foreach (var line in SdCardTextLineReader.ReadLinesAsync(source, CancellationToken.None))
        {
            Assert.Equal("alpha", line.Text);
            break;
        }

        var second = await ReadAllAsync(source);
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, second.Select(l => l.Text));
    }

    #endregion

    #region ToAsyncEnumerable

    [Fact]
    public async Task ToAsyncEnumerable_PreservesOrderAndContent()
    {
        var items = new[] { "alpha", "bravo", "charlie" };

        var seen = new List<string>();
        await foreach (var item in SdCardTextLineReader.ToAsyncEnumerable(items))
        {
            seen.Add(item);
        }

        Assert.Equal(items, seen);
    }

    [Fact]
    public async Task ToAsyncEnumerable_DoesNotTouchTheSourceUntilItIsEnumerated()
    {
        // The buffered path hands this a sequence that must not be walked eagerly.
        var sequence = SdCardTextLineReader.ToAsyncEnumerable(ThrowOnFirstMove());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sequence)
            {
            }
        });
    }

    private static IEnumerable<string> ThrowOnFirstMove()
    {
        throw new InvalidOperationException("enumerated");
#pragma warning disable CS0162 // Unreachable: the iterator body needs a yield to compile as one.
        yield break;
#pragma warning restore CS0162
    }

    #endregion

    #region Helpers

    private static async Task<List<SdCardLogLine>> ReadAllAsync(Stream stream)
    {
        var lines = new List<SdCardLogLine>();
        await foreach (var line in SdCardTextLineReader.ReadLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        return lines;
    }

    private static async Task<List<SdCardLogLine>> ReadAllAsync(SdCardParseSource source)
    {
        var lines = new List<SdCardLogLine>();
        await foreach (var line in SdCardTextLineReader.ReadLinesAsync(source, CancellationToken.None))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>A readable stream that reports <c>CanSeek == false</c>, like a live download.</summary>
    private sealed class ForwardOnlyStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A seekable stream that records how many times it was disposed.</summary>
    private sealed class DisposeCountingStream(byte[] payload) : MemoryStream(payload)
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    #endregion
}
