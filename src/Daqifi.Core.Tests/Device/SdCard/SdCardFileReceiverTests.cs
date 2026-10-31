using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public class SdCardFileReceiverTests
{
    private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");
    private static readonly byte[] TransferErrorMarker = Encoding.ASCII.GetBytes("__TRANSFER_ERROR__");

    /// <summary>
    /// How many bytes the receiver holds back from the destination stream while a marker could
    /// still be completing — the length of the LONGEST marker, not of the EOF one.
    /// </summary>
    private static readonly int TrailingWindow = Math.Max(EofMarker.Length, TransferErrorMarker.Length);

    [Fact]
    public async Task ReceiveAsync_CompleteFileWithEofMarker_WritesCorrectBytes()
    {
        // Arrange
        var fileData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var sourceData = Combine(fileData, EofMarker);
        using var sourceStream = new MemoryStream(sourceData);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "test.bin");

        // Assert
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_EofMarkerSplitAcrossChunks_DetectedCorrectly()
    {
        // Arrange — use a buffer size that will split the EOF marker across reads
        var fileData = new byte[10];
        for (var i = 0; i < fileData.Length; i++) fileData[i] = (byte)(i + 1);

        var sourceData = Combine(fileData, EofMarker);

        // Use a chunked stream that delivers data in small pieces
        using var sourceStream = new ChunkedMemoryStream(sourceData, chunkSize: 5);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 5);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "test.bin");

        // Assert
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_StreamGoesQuietBeforeEof_ThrowsStalledWithNoDataReceived()
    {
        // Arrange — a still-readable stream that stops producing data before the EOF marker.
        // On USB serial this is the ordinary stall: SerialStream.ReadAsync returns 0 on a read
        // timeout rather than throwing, so "the stream closed" would be factually wrong (#398 gap 1).
        var dataWithoutEof = new byte[] { 0x01, 0x02, 0x03 };
        using var sourceStream = new MemoryStream(dataWithoutEof);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));

        // Assert
        Assert.Equal(SdCardTransferStallReason.NoDataReceived, ex.Reason);
        Assert.Equal("test.bin", ex.FileName);
        Assert.Null(ex.Timeout);
    }

    [Fact]
    public async Task ReceiveAsync_TransportClosedBeforeEof_ThrowsStalledWithTransportClosed()
    {
        // Arrange — a transport that is genuinely no longer readable, as opposed to one that is
        // merely quiet. Retrying this download cannot succeed, so it gets its own reason.
        using var sourceStream = new ClosableStream(new byte[20]);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));

        // Assert — the last TrailingWindow (18) bytes are still held back in the trailing window
        // as a possible partial marker, so 2 of the 20 bytes had been written when the transport
        // closed. The count is the partial-file progress, not the bytes read.
        Assert.Equal(SdCardTransferStallReason.TransportClosed, ex.Reason);
        Assert.Equal(20 - TrailingWindow, ex.BytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_TransferTimeoutElapses_ThrowsStalledWithTransferTimeout()
    {
        // Arrange — a stream that never yields anything, so only the overall transfer deadline
        // ends the wait. The caller passes no cancellation token, so this is a timeout and must
        // not surface as a cancellation.
        using var sourceStream = new NeverEndingStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "test.bin", timeout: TimeSpan.FromMilliseconds(100)));

        // Assert
        Assert.Equal(SdCardTransferStallReason.TransferTimeout, ex.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(100), ex.Timeout);
        Assert.Equal(0, ex.BytesReceived);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task ReceiveAsync_StalledException_IsAnSdCardOperationException()
    {
        // A consumer that already handles SD failures by catching SdCardOperationException must
        // pick up stalls too — that is the point of typing them into the existing hierarchy.
        var dataWithoutEof = new byte[] { 0x01, 0x02, 0x03 };
        using var sourceStream = new MemoryStream(dataWithoutEof);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin"));

        Assert.IsAssignableFrom<SdCardOperationException>(ex);
    }

    [Fact]
    public async Task ReceiveAsync_CancellationTokenRespected_ThrowsOperationCanceledException()
    {
        // Arrange — stream that blocks (never returns data)
        using var sourceStream = new NeverEndingStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert — TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "test.bin",
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ReceiveAsync_WhenCancelledAndStreamIgnoresTheToken_ReportsCancellationNotTimeout()
    {
        // #399: System.IO.Ports' SerialStream ignores the token once a read is in flight, and on a
        // device that is sending nothing it just returns 0 bytes when its (500 ms) ReadTimeout
        // elapses. The loop translated that into "transport stream closed" — a timeout — even when
        // the caller had already cancelled, which is precisely the case where a consumer's stall
        // watchdog wants to see its own cancellation come back. Cancellation has to win.
        using var sourceStream = new TokenIgnoringSilentStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "test.bin",
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ReceiveAsync_ProgressReporting_BytesReceivedIncreases()
    {
        // Arrange
        var fileData = new byte[1000];
        new Random(42).NextBytes(fileData);
        var sourceData = Combine(fileData, EofMarker);

        using var sourceStream = new ChunkedMemoryStream(sourceData, chunkSize: 100);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 100);

        var progressReports = new System.Collections.Generic.List<SdCardTransferProgress>();
        var progress = new SynchronousProgress<SdCardTransferProgress>(p => progressReports.Add(p));

        // Act
        await receiver.ReceiveAsync(destinationStream, "test.bin", progress);

        // Assert — we should have received at least one progress report
        Assert.NotEmpty(progressReports);
        Assert.All(progressReports, p => Assert.Equal("test.bin", p.FileName));
    }

    [Fact]
    public async Task ReceiveAsync_MarkerOnlyTransfer_ThrowsSdCardEmptyTransferException()
    {
        // Arrange — only the EOF marker, no file data. A device whose SD subsystem is wedged
        // or not yet ready opens the file successfully but sends zero content bytes before the
        // marker — this must be surfaced as a failure, not a silent 0-byte "success" (#264).
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "empty.bin"));
        Assert.Equal("empty.bin", ex.FileName);
        Assert.Empty(destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_MarkerOnlyTransferSplitAcrossChunks_ThrowsSdCardEmptyTransferException()
    {
        // Arrange — same as above, but the marker itself is split across reads, exercising the
        // trailing-window path with zero file bytes ever written.
        using var sourceStream = new ChunkedMemoryStream(EofMarker, chunkSize: 5);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 5);

        // Act & Assert
        await Assert.ThrowsAsync<SdCardEmptyTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "empty.bin"));
    }

    [Fact]
    public async Task ReceiveAsync_MarkerOnlyTransferForListedNonEmptyFile_ThrowsWithListedSize()
    {
        // Arrange — the listing says 4096 bytes but the device sent none, so the SD subsystem
        // really is wedged and the guard can now say so with evidence (#398 gap 2).
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "wedged.bin", listedFileSizeBytes: 4096));

        // Assert
        Assert.Equal(4096, ex.ListedSizeInBytes);
        Assert.Contains("4096", ex.Message);
    }

    [Fact]
    public async Task ReceiveAsync_MarkerOnlyTransferForListedEmptyFile_ReturnsZeroBytes()
    {
        // Arrange — a genuinely 0-byte file, routinely left on a FAT card by an interrupted
        // logging session. It is indistinguishable on the wire from a wedged subsystem; only the
        // listing's reported size separates them, and it says the file really is empty.
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "empty.bin", listedFileSizeBytes: 0);

        // Assert
        Assert.Equal(0, bytesReceived);
        Assert.Empty(destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_MarkerOnlyTransferForListedEmptyFile_ReportsProgress()
    {
        // A legitimate empty download is a normal completion, so it reports terminal progress
        // like any other finished transfer rather than silently returning.
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var progressReports = new System.Collections.Generic.List<SdCardTransferProgress>();
        var progress = new SynchronousProgress<SdCardTransferProgress>(p => progressReports.Add(p));

        await receiver.ReceiveAsync(destinationStream, "empty.bin", progress, listedFileSizeBytes: 0);

        var report = Assert.Single(progressReports);
        Assert.Equal("empty.bin", report.FileName);
        Assert.Equal(0, report.BytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_UnknownListedSize_KeepsConservativeEmptyTransferBehavior()
    {
        // Arrange — with no listing to consult we cannot rule out a wedged subsystem, so the
        // #264 behavior stands and the caller's retry still covers it.
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardEmptyTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "unknown.bin", listedFileSizeBytes: null));

        // Assert
        Assert.Null(ex.ListedSizeInBytes);
    }

    [Fact]
    public async Task ReceiveAsync_ListedEmptyFileThatActuallyHasContent_StillReturnsTheContent()
    {
        // A stale listing must not truncate a real transfer: the listed size only gates the
        // marker-only case, it never limits what is written.
        var fileData = Encoding.ASCII.GetBytes("not actually empty");
        using var sourceStream = new MemoryStream(Combine(fileData, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "stale.bin", listedFileSizeBytes: 0);

        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_ScpiErrorLineInsteadOfFile_ThrowsSdCardTruncatedTransferException()
    {
        // The exact shape the bench Nq1 emits for a file its SD buffer can no longer serve: a
        // SCPI error line, then the end-of-file marker. Nothing on the wire distinguishes that
        // from a finished download, so before #539 this returned 34 "successful" bytes and the
        // caller wrote the error text to disk as the log file.
        var errorLine = Encoding.ASCII.GetBytes("**ERROR: -200, \"Execution error\"\r\n");
        using var sourceStream = new MemoryStream(Combine(errorLine, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var ex = await Assert.ThrowsAsync<SdCardTruncatedTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "log.bin", listedFileSizeBytes: 6159));

        Assert.Equal("log.bin", ex.FileName);
        Assert.Equal(6159, ex.ListedSizeInBytes);
        Assert.Equal(errorLine.Length, ex.BytesReceived);
        Assert.Contains("6159", ex.Message);
        Assert.Contains(errorLine.Length.ToString(CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public async Task ReceiveAsync_ShortTransferSpanningChunks_ThrowsWithTheTotalItActuallyReceived()
    {
        // A short transfer that arrives in several reads exercises the trailing-window accounting:
        // the reported byte count has to be the whole transfer, not just the final chunk.
        var partial = new byte[100];
        new Random(539).NextBytes(partial);
        using var sourceStream = new ChunkedMemoryStream(Combine(partial, EofMarker), chunkSize: 16);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 16);

        var ex = await Assert.ThrowsAsync<SdCardTruncatedTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "partial.bin", listedFileSizeBytes: 20118));

        Assert.Equal(100, ex.BytesReceived);
        Assert.Equal(20118, ex.ListedSizeInBytes);
    }

    [Fact]
    public async Task ReceiveAsync_TransferMatchingListedSize_Succeeds()
    {
        // The ordinary case, and the one the bench confirms byte-exact for every file the device
        // can actually serve: received == listed is a completed download and must not throw.
        var fileData = new byte[1539];
        new Random(1539).NextBytes(fileData);
        using var sourceStream = new MemoryStream(Combine(fileData, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "log.bin", listedFileSizeBytes: 1539);

        Assert.Equal(1539, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_TransferLongerThanListedSize_StillSucceeds()
    {
        // The size check is deliberately one-sided. A listing goes stale the moment an active
        // logging session appends to the file, and the extra bytes are real content — rejecting
        // them would turn a good download into a failure.
        var fileData = Encoding.ASCII.GetBytes("this file grew after it was listed");
        using var sourceStream = new MemoryStream(Combine(fileData, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "growing.bin", listedFileSizeBytes: 10);

        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_ShortTransferWithUnknownListedSize_CannotBeDetected()
    {
        // Documents the limit of the guard: with no listing there is nothing to compare against,
        // so a short transfer is indistinguishable from a small file and still returns. Fetching
        // a listing before downloading is what buys the check.
        var partial = Encoding.ASCII.GetBytes("**ERROR: -200, \"Execution error\"\r\n");
        using var sourceStream = new MemoryStream(Combine(partial, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "unlisted.bin", listedFileSizeBytes: null);

        Assert.Equal(partial.Length, bytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_LegacyPositionalCallShape_StillBinds()
    {
        // Compile-time regression guard: listedFileSizeBytes is deliberately appended AFTER
        // cancellationToken so the pre-existing positional shape
        // (dest, fileName, progress, timeout, cancellationToken) keeps binding for external
        // callers. This call is positional on purpose — do not convert it to named arguments.
        var fileData = new byte[] { 0x01, 0x02, 0x03 };
        using var sourceStream = new MemoryStream(Combine(fileData, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);
        using var cts = new CancellationTokenSource();

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "legacy.bin", null, TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(fileData.Length, bytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_NegativeListedSize_ThrowsArgumentOutOfRange()
    {
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => receiver.ReceiveAsync(destinationStream, "bad.bin", listedFileSizeBytes: -1));
    }

    [Fact]
    public async Task ReceiveAsync_LargeFile_AllDataReceivedCorrectly()
    {
        // Arrange — 64KB of data (larger than the 16KB default buffer and 32KB firmware buffer)
        var fileData = new byte[65536];
        new Random(42).NextBytes(fileData);
        var sourceData = Combine(fileData, EofMarker);

        using var sourceStream = new MemoryStream(sourceData);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "large.bin");

        // Assert
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_LargeFileWithSmallChunks_AllDataReceivedCorrectly()
    {
        // Arrange — simulate USB CDC chunks
        var fileData = new byte[50000];
        new Random(123).NextBytes(fileData);
        var sourceData = Combine(fileData, EofMarker);

        using var sourceStream = new ChunkedMemoryStream(sourceData, chunkSize: 512);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 1024);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "chunked.bin");

        // Assert
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_DataContainingPartialEofMarkerBytes_NotFalsePositive()
    {
        // Arrange — file data that contains bytes similar to the EOF marker but not the full marker
        var partialMarker = Encoding.ASCII.GetBytes("__END_OF_");
        var fileData = Combine(new byte[] { 0x01, 0x02 }, partialMarker, new byte[] { 0x03, 0x04 });
        var sourceData = Combine(fileData, EofMarker);

        using var sourceStream = new MemoryStream(sourceData);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "test.bin");

        // Assert — all file data should be received, partial marker is data not the terminator
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    #region FindTerminalMarker Tests

    [Fact]
    public void FindTerminalMarker_MarkerInNewData_ReturnsCorrectPosition()
    {
        // Arrange
        var trailing = new byte[0];
        var newData = Combine(new byte[] { 0x01, 0x02 }, EofMarker);

        // Act
        var (found, position, isTransferError) = SdCardFileReceiver.FindTerminalMarker(trailing, 0, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(2, position);
        Assert.False(isTransferError);
    }

    [Fact]
    public void FindTerminalMarker_MarkerSpanningBoundary_ReturnsCorrectPosition()
    {
        // Arrange — first 5 bytes of marker in trailing, rest in new data
        var trailing = new byte[EofMarker.Length];
        Array.Copy(EofMarker, 0, trailing, 0, 5);
        var trailingCount = 5;

        var newData = new byte[EofMarker.Length - 5];
        Array.Copy(EofMarker, 5, newData, 0, newData.Length);

        // Act
        var (found, position, isTransferError) = SdCardFileReceiver.FindTerminalMarker(trailing, trailingCount, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(0, position);
        Assert.False(isTransferError);
    }

    [Fact]
    public void FindTerminalMarker_NoMarker_ReturnsFalse()
    {
        // Arrange
        var trailing = new byte[] { 0x01, 0x02, 0x03 };
        var newData = new byte[] { 0x04, 0x05, 0x06 };

        // Act
        var (found, _, _) = SdCardFileReceiver.FindTerminalMarker(trailing, trailing.Length, newData, newData.Length);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void FindTerminalMarker_DataTooShort_ReturnsFalse()
    {
        // Arrange
        var trailing = new byte[0];
        var newData = new byte[] { 0x01 };

        // Act
        var (found, _, _) = SdCardFileReceiver.FindTerminalMarker(trailing, 0, newData, newData.Length);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void FindTerminalMarker_MarkerEntirelyInTrailing_NotDetected()
    {
        // The search starts at max(0, trailingCount - markerLen + 1) to avoid
        // re-detecting markers that were already fully in the trailing buffer.
        // Here the marker is at position 0 but trailing has 17 bytes (15 marker + 2 extra),
        // so searchStart = max(0, 17 - 15 + 1) = 3, skipping position 0.
        var trailing = new byte[EofMarker.Length + 2];
        Array.Copy(EofMarker, 0, trailing, 0, EofMarker.Length);
        trailing[EofMarker.Length] = 0xFF;
        trailing[EofMarker.Length + 1] = 0xFF;
        var newData = new byte[] { 0xAA };

        var (found, _, _) = SdCardFileReceiver.FindTerminalMarker(trailing, trailing.Length, newData, newData.Length);

        // The marker is entirely in the old trailing data — not found in the new scan
        Assert.False(found);
    }

    #endregion

    #region Transfer-error marker (#720)

    [Fact]
    public async Task ReceiveAsync_TransferErrorMarker_ThrowsWithPartialContentWritten()
    {
        // Arrange — firmware v3.7.3 ends a mid-transfer card read failure with its own marker
        // instead of going silent. What preceded it is real file content.
        var fileData = Encoding.ASCII.GetBytes("partial log content that did arrive");
        using var sourceStream = new MemoryStream(Combine(fileData, TransferErrorMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferErrorException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));

        // Assert
        Assert.Equal("test.bin", ex.FileName);
        Assert.Equal(fileData.Length, ex.BytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_TransferErrorMarker_IsStrippedFromOutput()
    {
        // Arrange — the marker must never reach the destination stream: writing its 18 bytes
        // would silently corrupt the tail of the partial file (#720).
        var fileData = Encoding.ASCII.GetBytes("Hello, World!");
        using var sourceStream = new MemoryStream(Combine(fileData, TransferErrorMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        await Assert.ThrowsAsync<SdCardTransferErrorException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));

        // Assert
        var output = Encoding.ASCII.GetString(destinationStream.ToArray());
        Assert.Equal("Hello, World!", output);
        Assert.DoesNotContain("__TRANSFER_ERROR__", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__TRANSFER", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveAsync_TransferErrorMarkerSplitAcrossChunks_DetectedCorrectly()
    {
        // Arrange — the marker is longer than the EOF one, so a trailing window sized for the
        // EOF marker would leak its first bytes into the file and then never match the rest.
        var fileData = new byte[40];
        for (var i = 0; i < fileData.Length; i++) fileData[i] = (byte)(i + 1);

        using var sourceStream = new ChunkedMemoryStream(
            Combine(fileData, TransferErrorMarker), chunkSize: 7);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: 7);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferErrorException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));

        // Assert
        Assert.Equal(fileData.Length, ex.BytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_TransferErrorMarkerWithNoContent_ThrowsWithZeroBytes()
    {
        // Arrange — a read that failed on the very first block. It is still a device read error,
        // not the wedged-subsystem case SdCardEmptyTransferException describes.
        using var sourceStream = new MemoryStream(TransferErrorMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferErrorException>(
            () => receiver.ReceiveAsync(
                destinationStream,
                "test.bin",
                timeout: TimeSpan.FromSeconds(5),
                listedFileSizeBytes: 4096));

        // Assert
        Assert.Equal(0, ex.BytesReceived);
        Assert.Empty(destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_TransferErrorMarkerShortOfListedSize_PrefersTransferError()
    {
        // Arrange — the transfer IS short of the listing, but the device said why. Reporting it
        // as a truncated transfer would hide the actual cause behind the size comparison.
        var fileData = new byte[64];
        using var sourceStream = new MemoryStream(Combine(fileData, TransferErrorMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var ex = await Assert.ThrowsAsync<SdCardTransferErrorException>(
            () => receiver.ReceiveAsync(
                destinationStream,
                "test.bin",
                timeout: TimeSpan.FromSeconds(5),
                listedFileSizeBytes: 4096));

        // Assert
        Assert.Equal(fileData.Length, ex.BytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_EofMarkerAfterTransferErrorLikeContent_StillCompletes()
    {
        // Arrange — content that merely starts like the error marker must not end the transfer.
        var fileData = Encoding.ASCII.GetBytes("__TRANSFER_ERROR_NOT_REALLY");
        using var sourceStream = new MemoryStream(Combine(fileData, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "test.bin");

        // Assert
        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public void FindTerminalMarker_TransferErrorMarkerInNewData_ReportsTransferError()
    {
        // Arrange
        var trailing = new byte[0];
        var newData = Combine(new byte[] { 0x01, 0x02 }, TransferErrorMarker);

        // Act
        var (found, position, isTransferError) = SdCardFileReceiver.FindTerminalMarker(
            trailing, 0, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(2, position);
        Assert.True(isTransferError);
    }

    [Fact]
    public void FindTerminalMarker_BothMarkersPresent_ReturnsTheEarlierOne()
    {
        // Arrange — a read error that lands in the same chunk as a later EOF marker. The first
        // terminator is the one that ended the transfer.
        var trailing = new byte[0];
        var newData = Combine(new byte[] { 0x01 }, TransferErrorMarker, EofMarker);

        // Act
        var (found, position, isTransferError) = SdCardFileReceiver.FindTerminalMarker(
            trailing, 0, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(1, position);
        Assert.True(isTransferError);
    }

    #endregion

    #region WiFi/TCP transport (#327)

    [Fact]
    public async Task ReceiveAsync_TcpSizedChunks_ReceivesTheWholeFile()
    {
        // The firmware clamps every SD reply chunk it writes to the TCP buffer at 1024 bytes
        // (#599), so a WiFi download arrives as a long run of short reads no matter how large the
        // receive buffer is.
        var fileData = new byte[50_000];
        new Random(1327).NextBytes(fileData);
        using var sourceStream = new ChunkedMemoryStream(Combine(fileData, EofMarker), chunkSize: 1024);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, zeroLengthReadMeansClosed: true);

        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "wifi.bin");

        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task ReceiveAsync_EofMarkerSplitAtAnyOffsetOfATcpChunk_DetectedCorrectly(int bytesOfMarkerInFirstChunk)
    {
        // The 1024-byte clamp lands wherever the file's length puts it, so the terminator can be
        // cut at any offset. Size the file so the chunk boundary falls that many bytes into the
        // marker, and check the split is invisible to the caller.
        var fileLength = 1024 - bytesOfMarkerInFirstChunk + 1024;
        var fileData = new byte[fileLength];
        new Random(bytesOfMarkerInFirstChunk).NextBytes(fileData);

        using var sourceStream = new ChunkedMemoryStream(Combine(fileData, EofMarker), chunkSize: 1024);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, zeroLengthReadMeansClosed: true);

        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "wifi.bin");

        Assert.Equal(fileLength, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_ThrottledTcpDelivery_KeepsGoingWhileChunksArrive()
    {
        // The firmware's SD task and the WiFi task that drains the TCP buffer run at different
        // priorities, so chunks arrive with gaps. The inactivity window must be a per-gap budget
        // that each chunk resets, not a total the whole transfer has to fit inside.
        var fileData = Encoding.ASCII.GetBytes(new string('x', 40));
        using var sourceStream = new ThrottledStream(
            Combine(fileData, EofMarker), chunkSize: 8, gap: TimeSpan.FromMilliseconds(30));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(
            sourceStream, zeroLengthReadMeansClosed: true, idleTimeout: TimeSpan.FromSeconds(2));

        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "wifi.bin");

        Assert.Equal(fileData.Length, bytesReceived);
        Assert.Equal(fileData, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_TransportGoesSilentWithoutReturningZero_StallsOnTheIdleWindow()
    {
        // A socket never reports silence: NetworkStream.ReadAsync ignores Socket.ReceiveTimeout, so
        // a device that stops answering mid-file leaves the read parked. Without the inactivity
        // window the transfer would sit there for the caller's whole download budget.
        using var sourceStream = new NeverEndingStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(
            sourceStream, zeroLengthReadMeansClosed: true, idleTimeout: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "wifi.bin", timeout: TimeSpan.FromMinutes(30)));

        Assert.Equal(SdCardTransferStallReason.NoDataReceived, ex.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ex.Timeout);
        Assert.Contains("stopped feeding the transfer", ex.Message);
    }

    [Fact]
    public async Task ReceiveAsync_IdleWindowDisabled_LeavesTheOverallDeadlineInCharge()
    {
        using var sourceStream = new NeverEndingStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(
            sourceStream, zeroLengthReadMeansClosed: true, idleTimeout: Timeout.InfiniteTimeSpan);

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "wifi.bin", timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal(SdCardTransferStallReason.TransferTimeout, ex.Reason);
    }

    [Fact]
    public async Task ReceiveAsync_ZeroLengthReadOnANetworkTransport_ReportsTransportClosed()
    {
        // NetworkStream.CanRead stays true after the peer's FIN, so the stream cannot be asked what
        // an empty read means — only the caller knows. On a socket it can mean one thing.
        using var sourceStream = new TokenIgnoringSilentStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, zeroLengthReadMeansClosed: true);

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(destinationStream, "wifi.bin", timeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(SdCardTransferStallReason.TransportClosed, ex.Reason);
    }

    [Fact]
    public async Task ReceiveAsync_ZeroLengthReadOnSerial_StillReportsNoDataReceived()
    {
        // Regression guard for the USB path: there an empty read is the ordinary per-read timeout
        // on a quiet device, and calling that a closed transport would tell callers not to retry
        // something that is entirely retryable (#398 gap 1).
        using var sourceStream = new TokenIgnoringSilentStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, zeroLengthReadMeansClosed: false);

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(destinationStream, "usb.bin", timeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(SdCardTransferStallReason.NoDataReceived, ex.Reason);
        Assert.Null(ex.Timeout);
    }

    [Fact]
    public void Ctor_OriginalTwoParameterSignature_StillExistsForCompiledCallers()
    {
        // Optional arguments are compile-time only: adding parameters to the (Stream, int)
        // constructor would have kept every source caller compiling while leaving already-built
        // consumers of the Daqifi.Core package calling a CLR signature that no longer exists.
        // Assert on the metadata, not on a call, because a source-level call would bind happily
        // to a widened constructor and prove nothing.
        var ctor = typeof(SdCardFileReceiver).GetConstructor(new[] { typeof(Stream), typeof(int) });

        Assert.NotNull(ctor);
    }

    [Fact]
    public async Task Ctor_OriginalSignature_LeavesTheCallersDeadlineInCharge()
    {
        // Restoring the CLR signature is only half of what an old caller is owed. The other half is
        // that it still DOES what it did: before the inactivity window existed, a transport that
        // went quiet without returning zero bytes ran until the caller's own deadline. Delegating
        // with the new default would have switched on a 20-second window nobody asked for and
        // started abandoning those transfers — a silent change, and precisely to the callers this
        // overload exists to protect.
        using var sourceStream = new NeverEndingStream();
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var ex = await Assert.ThrowsAsync<SdCardTransferStalledException>(
            () => receiver.ReceiveAsync(
                destinationStream, "legacy.bin", timeout: TimeSpan.FromMilliseconds(400)));

        // The caller's deadline ended it, not an idle window: an active window shorter than 400 ms
        // would report NoDataReceived and its own duration instead.
        Assert.Equal(SdCardTransferStallReason.TransferTimeout, ex.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(400), ex.Timeout);
    }

    [Fact]
    public void Ctor_OriginalSignature_LeavesTheInactivityWindowOff()
    {
        // The behavioral test above can only rule out a window shorter than its own deadline, and
        // the default is 20 seconds — too long to wait out in a unit test. So assert the state
        // directly, the same way the signature test asserts on metadata: a null window is "off",
        // and this fails for ANY window, not just a short one.
        using var stream = new MemoryStream();

        Assert.Null(IdleTimeoutOf(new SdCardFileReceiver(stream)));

        // Control: the opt-in overload does get the default, so this is pinning the difference
        // between the two constructors rather than asserting the feature is simply never on.
        Assert.Equal(
            SdCardFileReceiver.DefaultIdleTimeout,
            IdleTimeoutOf(new SdCardFileReceiver(stream, zeroLengthReadMeansClosed: true)));
    }

    private static TimeSpan? IdleTimeoutOf(SdCardFileReceiver receiver)
    {
        var field = typeof(SdCardFileReceiver).GetField(
            "_idleTimeout", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TimeSpan?)field!.GetValue(receiver);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveIdleTimeout_Throws(int seconds)
    {
        using var sourceStream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SdCardFileReceiver(
                sourceStream, zeroLengthReadMeansClosed: false, idleTimeout: TimeSpan.FromSeconds(seconds)));
    }

    #endregion

    #region End-of-file marker inside the file's own content (#852)

    /// <summary>
    /// What the receiver is expected to do with one shape of SD reply.
    /// </summary>
    private enum MarkerOutcome
    {
        /// <summary>The whole file arrives and the call returns its byte count.</summary>
        Complete,

        /// <summary>The end of the file cannot be located, and the call says so.</summary>
        Ambiguous,

        /// <summary>The reply ended without a terminator, and the call says so.</summary>
        Stalled,
    }

    /// <summary>
    /// One row per shape the end-of-file marker can take in an SD reply, including the shapes
    /// where the marker text is part of the file's own content.
    /// </summary>
    public static TheoryData<string> EndOfFileMarkerShapes() => new()
    {
        "plain file, listed size",
        "marker text inside the content, listed size",
        "marker text inside the content, no listed size",
        "marker text split across a read boundary, listed size",
        "partial marker text at the end of the stream",
        "content ends exactly on a read boundary, listed size",
        "partial marker text immediately before the real marker, no listed size",
    };

    [Theory]
    [MemberData(nameof(EndOfFileMarkerShapes))]
    public async Task ReceiveAsync_EndOfFileMarkerShapes_ReturnsTheWholeFileOrSaysWhyNot(string shape)
    {
        // The marker is ASCII text, so a log file can contain it. The listed size is then the only
        // authority on where the file ends: with a size the receiver copies that many bytes and
        // treats the text as content; without one it cannot tell content from terminator, and
        // says so rather than returning a short file (#852).
        var (sourceData, expectedContent, chunkSize, listedSize, outcome) = MarkerShape(shape);

        using var sourceStream = new ChunkedMemoryStream(sourceData, chunkSize);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: chunkSize);

        if (outcome == MarkerOutcome.Complete)
        {
            var bytesReceived = await receiver.ReceiveAsync(
                destinationStream,
                "log.csv",
                timeout: TimeSpan.FromSeconds(10),
                listedFileSizeBytes: listedSize);

            Assert.Equal(expectedContent.Length, bytesReceived);
            Assert.Equal(expectedContent, destinationStream.ToArray());
            return;
        }

        var ex = await Assert.ThrowsAnyAsync<SdCardOperationException>(
            () => receiver.ReceiveAsync(
                destinationStream,
                "log.csv",
                timeout: TimeSpan.FromSeconds(10),
                listedFileSizeBytes: listedSize));

        if (outcome == MarkerOutcome.Ambiguous)
        {
            var ambiguous = Assert.IsType<SdCardAmbiguousTransferException>(ex);
            Assert.Equal("log.csv", ambiguous.FileName);
            Assert.Equal(expectedContent.Length, ambiguous.BytesReceived);
        }
        else
        {
            Assert.IsType<SdCardTransferStalledException>(ex);
        }
    }

    /// <summary>
    /// Builds one row of <see cref="EndOfFileMarkerShapes"/>: the bytes the device sends, the file
    /// content the caller must end up with (or, for an ambiguous reply, the content that precedes
    /// the first marker), the read size, the listed size, and the expected outcome.
    /// </summary>
    private static (byte[] SourceData, byte[] ExpectedContent, int ChunkSize, long? ListedSize, MarkerOutcome Outcome)
        MarkerShape(string shape)
    {
        // A line of log text that holds the marker, exactly as a note typed into a log would.
        var head = Encoding.ASCII.GetBytes("note before ");
        var tail = Encoding.ASCII.GetBytes(" was written here\r\n1,0.5\r\n");
        var contentWithMarker = Combine(head, EofMarker, tail);
        var plainContent = Encoding.ASCII.GetBytes("timestamp,ch0\r\n1,0.50\r\n2,0.75\r\n3,1.00\r\n");
        var partialMarker = Encoding.ASCII.GetBytes("__END_OF_FIL");

        return shape switch
        {
            "plain file, listed size" => (
                Combine(plainContent, EofMarker),
                plainContent,
                16,
                plainContent.Length,
                MarkerOutcome.Complete),

            // The losing case the ticket reports: with a listed size this threw on every attempt,
            // so the file could not be downloaded at all.
            "marker text inside the content, listed size" => (
                Combine(contentWithMarker, EofMarker),
                contentWithMarker,
                4096,
                contentWithMarker.Length,
                MarkerOutcome.Complete),

            // The other losing case: with no listed size this returned the 12 bytes before the
            // marker as a finished download and left the rest of the file on the wire.
            "marker text inside the content, no listed size" => (
                Combine(contentWithMarker, EofMarker),
                head,
                4096,
                null,
                MarkerOutcome.Ambiguous),

            // Six bytes of the marker land in the first read and the other nine in the second, so
            // a scan that only looks inside one read would miss it or cut it in half.
            "marker text split across a read boundary, listed size" => (
                Combine(contentWithMarker, EofMarker),
                contentWithMarker,
                head.Length + 6,
                contentWithMarker.Length,
                MarkerOutcome.Complete),

            // Twelve of the marker's fifteen bytes, and then silence. A partial marker is not a
            // marker, so the reply ended without a terminator.
            "partial marker text at the end of the stream" => (
                Combine(plainContent, partialMarker),
                plainContent,
                16,
                null,
                MarkerOutcome.Stalled),

            "content ends exactly on a read boundary, listed size" => (
                Combine(plainContent, EofMarker),
                plainContent,
                plainContent.Length,
                plainContent.Length,
                MarkerOutcome.Complete),

            // The content ends with twelve bytes of the marker and the real marker follows it, so
            // the two run together on the wire. The first complete marker starts at the real one.
            "partial marker text immediately before the real marker, no listed size" => (
                Combine(plainContent, partialMarker, EofMarker),
                Combine(plainContent, partialMarker),
                16,
                null,
                MarkerOutcome.Complete),

            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
        };
    }

    [Fact]
    public async Task ReceiveAsync_MarkerInContentWithListedSize_WritesTheMarkerTextAsFileContent()
    {
        // The byte-exact form of the ticket: 57 bytes of content that hold the marker text, and a
        // listing that says 57. Before the fix the call threw SdCardTruncatedTransferException
        // after 12 bytes and the file was unreachable; now all 57 bytes arrive, marker text and
        // all.
        var content = Encoding.ASCII.GetBytes("note before __END_OF_FILE__ was written here\r\n1,0.5\r\n");
        using var sourceStream = new MemoryStream(Combine(content, EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "log.csv", listedFileSizeBytes: content.Length);

        Assert.Equal(content.Length, bytesReceived);
        Assert.Equal(content, destinationStream.ToArray());
        Assert.Contains(
            "__END_OF_FILE__",
            Encoding.ASCII.GetString(destinationStream.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveAsync_MarkerInContentNoListedSize_ContentFollowsInTheNextRead_ThrowsAmbiguous()
    {
        // The read ends on the marker's last byte, so nothing in that read shows the file goes on.
        // The receiver has to look at what comes next before it accepts the marker.
        var head = Encoding.ASCII.GetBytes("note before ");
        var tail = Encoding.ASCII.GetBytes(" was written here\r\n");
        using var sourceStream = new ChunkedMemoryStream(
            Combine(head, EofMarker, tail, EofMarker), chunkSize: head.Length + EofMarker.Length);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream, bufferSize: head.Length + EofMarker.Length);

        var ex = await Assert.ThrowsAsync<SdCardAmbiguousTransferException>(
            () => receiver.ReceiveAsync(
                destinationStream, "log.csv", timeout: TimeSpan.FromSeconds(10)));

        Assert.Equal(head.Length, ex.BytesReceived);
        Assert.Contains("log.csv", ex.Message, StringComparison.Ordinal);
        Assert.Contains("__END_OF_FILE__", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveAsync_AmbiguousTransfer_IsAnSdCardOperationException()
    {
        // Callers catch SdCardOperationException around a download, so the new failure has to sit
        // inside that family like every other SD card failure.
        var head = Encoding.ASCII.GetBytes("note before ");
        using var sourceStream = new MemoryStream(
            Combine(head, EofMarker, Encoding.ASCII.GetBytes("more"), EofMarker));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var ex = await Assert.ThrowsAsync<SdCardAmbiguousTransferException>(
            () => receiver.ReceiveAsync(destinationStream, "log.csv"));

        Assert.IsAssignableFrom<SdCardOperationException>(ex);
    }

    [Fact]
    public async Task ReceiveAsync_MarkerFollowedByLineEnding_NoListedSize_StillCompletes()
    {
        // The device can put a line ending after the marker. That is not file content, so the
        // download is complete and must not be reported as ambiguous.
        var content = Encoding.ASCII.GetBytes("timestamp,ch0\r\n1,0.50\r\n");
        using var sourceStream = new MemoryStream(
            Combine(content, EofMarker, Encoding.ASCII.GetBytes("\r\n")));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "log.csv");

        Assert.Equal(content.Length, bytesReceived);
        Assert.Equal(content, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_MarkerFollowedByLineEndingInTheNextRead_NoListedSize_StillCompletes()
    {
        // Same line ending, but it arrives in a read of its own, so it is only visible to the
        // check that looks at what follows the marker.
        var content = Encoding.ASCII.GetBytes("timestamp,ch0\r\n1,0.50\r\n");
        using var sourceStream = new ChunkedMemoryStream(
            Combine(content, EofMarker, Encoding.ASCII.GetBytes("\r\n")),
            chunkSize: content.Length + EofMarker.Length);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(
            sourceStream, bufferSize: content.Length + EofMarker.Length);

        var bytesReceived = await receiver.ReceiveAsync(
            destinationStream, "log.csv", timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(content.Length, bytesReceived);
        Assert.Equal(content, destinationStream.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_ShortReplyOnASocket_StillReportsTheTruncatedTransfer()
    {
        // A short reply ends in silence, and on a socket silence only shows up as the inactivity
        // window closing. The receiver must still name the short transfer (#539) there, not the
        // transport.
        var errorLine = Encoding.ASCII.GetBytes("**ERROR: -200, \"Execution error\"\r\n");
        using var sourceStream = new ThrottledStream(
            Combine(errorLine, EofMarker), chunkSize: 64, gap: TimeSpan.FromMilliseconds(1));
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(
            sourceStream,
            zeroLengthReadMeansClosed: true,
            idleTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<SdCardTruncatedTransferException>(
            () => receiver.ReceiveAsync(
                destinationStream,
                "log.csv",
                timeout: TimeSpan.FromSeconds(10),
                listedFileSizeBytes: 6159));

        Assert.Equal(6159, ex.ListedSizeInBytes);
        Assert.Equal(errorLine.Length, ex.BytesReceived);
    }

    #endregion

    #region Helper Methods

    private static byte[] Combine(params byte[][] arrays)
    {
        var totalLength = 0;
        foreach (var arr in arrays) totalLength += arr.Length;

        var result = new byte[totalLength];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Array.Copy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }

        return result;
    }

    #endregion

    #region Helper Streams

    /// <summary>
    /// A MemoryStream wrapper that returns data in fixed-size chunks to simulate
    /// real transport behavior where data arrives incrementally.
    /// </summary>
    private sealed class ChunkedMemoryStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedMemoryStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = _data.Length - _position;
            if (available <= 0) return 0;

            var toRead = Math.Min(Math.Min(count, _chunkSize), available);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A silent device: every read returns 0 bytes (a serial read timeout) and the cancellation
    /// token is accepted and then ignored, exactly as System.IO.Ports' SerialStream behaves for a
    /// read already in flight.
    /// </summary>
    private sealed class TokenIgnoringSilentStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Deliberately no ThrowIfCancellationRequested: the token is accepted, never acted on.
            return Task.FromResult(0);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that serves a fixed payload and then reports itself unreadable, standing in for
    /// a transport that was genuinely closed underneath the transfer (as opposed to one that is
    /// merely quiet, which is what a serial read timeout looks like).
    /// </summary>
    private sealed class ClosableStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        private bool _closed;

        public ClosableStream(byte[] data)
        {
            _data = data;
        }

        public override bool CanRead => !_closed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = _data.Length - _position;
            if (available <= 0)
            {
                _closed = true;
                return 0;
            }

            var toRead = Math.Min(count, available);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that hands out small chunks with a pause between them and never returns an empty
    /// read, the way a socket delivers a throttled SD reply.
    /// </summary>
    private sealed class ThrottledStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _gap;
        private int _position;

        public ThrottledStream(byte[] data, int chunkSize, TimeSpan gap)
        {
            _data = data;
            _chunkSize = chunkSize;
            _gap = gap;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(_gap, cancellationToken);

            var available = _data.Length - _position;
            if (available <= 0)
            {
                // Past the payload the device simply says nothing more, as a socket would.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var toRead = Math.Min(Math.Min(count, _chunkSize), available);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that blocks on read until cancellation is requested.
    /// Used to test cancellation behavior.
    /// </summary>
    private sealed class NeverEndingStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
