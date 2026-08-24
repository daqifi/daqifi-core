using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct tests for <see cref="SdCardParseSource"/> and its <see cref="SdCardParseSource.Lease"/>,
/// the rewind-and-re-read primitive that lets an SD card log session stay lazy.
/// </summary>
/// <remarks>
/// <para>
/// The three log parsers read a short prefix to work out the device configuration, then hand the
/// session an iterator that asks this source to re-open the log from the start on every
/// enumeration. That is what keeps a multi-megabyte download down to one read buffer of resident
/// memory — and it is entirely this class's job to make "from the start" mean the right thing and
/// to stop two readers sharing one cursor.
/// </para>
/// <para>
/// It had no tests of its own. The parser fixtures reach it only along the happy path: a
/// <see cref="MemoryStream"/> positioned at zero, enumerated once, from a source that is never
/// re-opened while a read is in flight. So the parts that only matter when something goes wrong
/// were unpinned — the rewind target for a stream handed over mid-way, the single-cursor latch and
/// its release, the ownership rule that decides whether disposing a lease closes the caller's
/// stream, and the two argument guards.
/// </para>
/// </remarks>
public class SdCardParseSourceTests
{
    private const string Content = "alpha\nbravo\ncharlie\n";

    #region TryCreate

    [Fact]
    public void TryCreate_WithNullStream_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => SdCardParseSource.TryCreate(null!));
        Assert.Equal("stream", ex.ParamName);
    }

    /// <summary>
    /// A forward-only stream cannot be rewound, so it cannot back a lazy session at all. The
    /// caller distinguishes the two worlds by the <c>null</c>, and buffers the whole log instead.
    /// </summary>
    [Fact]
    public void TryCreate_WithAForwardOnlyStream_ReturnsNull()
    {
        using var stream = new ForwardOnlyStream(Encoding.ASCII.GetBytes(Content));

        Assert.Null(SdCardParseSource.TryCreate(stream));
    }

    /// <summary>
    /// The source rewinds to where the caller's stream was when it was handed over, not to byte
    /// zero — a parser may have been given a log embedded partway through a larger stream. The
    /// reported total, by contrast, is the whole stream's length, which is what the absolute
    /// positions the line reader emits are measured against.
    /// </summary>
    [Fact]
    public async Task Open_RewindsToWhereTheStreamWasWhenItWasWrapped_NotToByteZero()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        stream.Position = 6; // Start of "bravo".

        var source = SdCardParseSource.TryCreate(stream);
        Assert.NotNull(source);
        Assert.Equal((long)Content.Length, source!.TotalBytes);

        Assert.Equal("bravo\ncharlie\n", await ReadAllAsync(source));

        // ...and again, because a session re-enumerates from the same origin every time.
        Assert.Equal("bravo\ncharlie\n", await ReadAllAsync(source));
    }

    #endregion

    #region The single-cursor latch

    /// <summary>
    /// Two overlapping reads of one caller-supplied stream would interleave on a single read
    /// cursor and hand back silently corrupt samples. The second read is refused instead.
    /// </summary>
    [Fact]
    public async Task Open_WhileALeaseOverACallerStreamIsOutstanding_Throws()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        var lease = source.Open();
        await using (lease.ConfigureAwait(false))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => source.Open());
            Assert.Contains("already being read", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Open_AfterTheOutstandingLeaseIsDisposed_Succeeds()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        var first = source.Open();
        await first.DisposeAsync();

        var second = source.Open();
        await using (second.ConfigureAwait(false))
        {
            Assert.Equal(0L, second.Stream.Position);
        }
    }

    /// <summary>
    /// Disposing a lease twice must not release a lease someone else took in between. An async
    /// iterator can be disposed by both its own <c>finally</c> and an outer <c>await using</c>,
    /// so a stale second dispose is a realistic event rather than a contrived one — and without
    /// the idempotence guard it would silently unlatch the live read and let the corruption the
    /// latch exists to prevent happen anyway.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotReleaseALeaseTakenInBetween()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        var first = source.Open();
        await first.DisposeAsync();

        var second = source.Open();
        await first.DisposeAsync(); // Stale: `first` no longer holds anything.

        Assert.Throws<InvalidOperationException>(() => source.Open());

        await second.DisposeAsync();
        await using var third = source.Open();
    }

    /// <summary>
    /// Rewinding can fail — a stream over a network or a device file may refuse to seek even
    /// though it claims it can. When it does, the latch has to come back off, or the source is
    /// bricked and every later read reports "already being read" for a read that never started.
    /// </summary>
    [Fact]
    public async Task Open_WhenRewindingFails_LeavesTheSourceUsable()
    {
        using var stream = new SeekRefusingStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        stream.FailNextSeek = true;
        Assert.Throws<IOException>(() => source.Open());

        var lease = source.Open();
        await using (lease.ConfigureAwait(false))
        {
            Assert.Equal(0L, lease.Stream.Position);
        }
    }

    #endregion

    #region Stream ownership

    /// <summary>
    /// The source borrows a caller-supplied stream; the caller still owns it and may keep reading
    /// after the session is finished with it.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_OverACallerSuppliedStream_LeavesItOpen()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(Content));
        var source = SdCardParseSource.TryCreate(stream)!;

        var lease = source.Open();
        Assert.Same(stream, lease.Stream);
        await lease.DisposeAsync();

        stream.Position = 0;
        Assert.Equal((int)'a', stream.ReadByte());
    }

    /// <summary>
    /// A file source opens the stream itself, so disposing the lease has to close it — otherwise
    /// a session that re-enumerates leaks a file handle per pass.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_OverAFileSource_ClosesTheStreamItOpened()
    {
        var path = WriteTempFile();
        try
        {
            var source = SdCardParseSource.ForFile(path, 4096);

            var lease = source.Open();
            await lease.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => lease.Stream.ReadByte());
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region ForFile

    [Fact]
    public void ForFile_WithNullPath_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => SdCardParseSource.ForFile(null!, 4096));
        Assert.Equal("filePath", ex.ParamName);
    }

    /// <summary>
    /// A file source does not touch the filesystem until it is first opened, so it has to report
    /// an unknown size until then rather than guessing at zero — zero would read as "empty log,
    /// nothing to download" to anything driving a progress bar off it.
    /// </summary>
    [Fact]
    public async Task ForFile_LearnsItsTotalSizeOnTheFirstOpen()
    {
        var path = WriteTempFile();
        try
        {
            var source = SdCardParseSource.ForFile(path, 4096);
            Assert.Equal(-1L, source.TotalBytes);

            await using (source.Open())
            {
                Assert.Equal((long)Content.Length, source.TotalBytes);
            }

            Assert.Equal((long)Content.Length, source.TotalBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A missing file surfaces as the filesystem's own exception on <see cref="SdCardParseSource.Open"/>,
    /// and the size stays unknown rather than being left at whatever a half-finished open saw.
    /// </summary>
    [Fact]
    public void ForFile_OpeningAMissingFile_ThrowsAndLeavesTheSizeUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daqifi-absent-{Guid.NewGuid():N}.csv");
        var source = SdCardParseSource.ForFile(path, 4096);

        Assert.Throws<FileNotFoundException>(() => source.Open());
        Assert.Equal(-1L, source.TotalBytes);
    }

    /// <summary>
    /// The single-cursor latch applies only to a borrowed stream. A file source opens an
    /// independent handle per read, so two enumerations may legitimately overlap — the case the
    /// exception message points callers at as the way out.
    /// </summary>
    [Fact]
    public async Task ForFile_SupportsTwoOverlappingReads()
    {
        var path = WriteTempFile();
        try
        {
            var source = SdCardParseSource.ForFile(path, 4096);

            var first = source.Open();
            await using (first.ConfigureAwait(false))
            {
                var second = source.Open();
                await using (second.ConfigureAwait(false))
                {
                    Assert.Equal(Content, await ReadAllAsync(second.Stream));
                }

                Assert.Equal(Content, await ReadAllAsync(first.Stream));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Helpers

    private static string WriteTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"daqifi-parse-source-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, Content);
        return path;
    }

    private static async Task<string> ReadAllAsync(SdCardParseSource source)
    {
        var lease = source.Open();
        await using (lease.ConfigureAwait(false))
        {
            return await ReadAllAsync(lease.Stream);
        }
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>A stream that reports it cannot seek, like a network or pipe stream.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>
    /// A seekable stream whose next seek fails, standing in for a stream that advertises seeking
    /// but cannot actually deliver it.
    /// </summary>
    private sealed class SeekRefusingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool FailNextSeek { get; set; }

        public override long Position
        {
            get => base.Position;
            set
            {
                if (FailNextSeek)
                {
                    FailNextSeek = false;
                    throw new IOException("Seek refused.");
                }

                base.Position = value;
            }
        }
    }

    #endregion
}
