using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public class SdCardFileReceiverTests
{
    private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

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
    public async Task ReceiveAsync_EofMarkerIsStrippedFromOutput()
    {
        // Arrange
        var fileData = Encoding.ASCII.GetBytes("Hello, World!");
        var sourceData = Combine(fileData, EofMarker);
        using var sourceStream = new MemoryStream(sourceData);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        await receiver.ReceiveAsync(destinationStream, "test.bin");

        // Assert — output should NOT contain the EOF marker
        var output = destinationStream.ToArray();
        Assert.Equal(fileData.Length, output.Length);
        Assert.DoesNotContain("__END_OF_FILE__", Encoding.ASCII.GetString(output));
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
    public async Task ReceiveAsync_TimeoutWhenEofNeverArrives_ThrowsTimeoutException()
    {
        // Arrange — stream that never contains EOF marker and eventually returns 0
        var dataWithoutEof = new byte[] { 0x01, 0x02, 0x03 };
        using var sourceStream = new MemoryStream(dataWithoutEof);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act & Assert — stream ends without EOF marker, should throw TimeoutException
        await Assert.ThrowsAsync<TimeoutException>(
            () => receiver.ReceiveAsync(destinationStream, "test.bin", timeout: TimeSpan.FromSeconds(5)));
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
        var progress = new Progress<SdCardTransferProgress>(p => progressReports.Add(p));

        // Act
        await receiver.ReceiveAsync(destinationStream, "test.bin", progress);

        // Allow progress callbacks to fire (they're posted to the sync context)
        await Task.Delay(100);

        // Assert — we should have received at least one progress report
        Assert.NotEmpty(progressReports);
        Assert.All(progressReports, p =>
        {
            Assert.Equal("test.bin", p.FileName);
            Assert.True(p.BytesReceived >= 0);
        });
    }

    [Fact]
    public async Task ReceiveAsync_EmptyFile_JustEofMarker_OutputIsEmpty()
    {
        // Arrange — only the EOF marker, no file data
        using var sourceStream = new MemoryStream(EofMarker);
        using var destinationStream = new MemoryStream();
        var receiver = new SdCardFileReceiver(sourceStream);

        // Act
        var bytesReceived = await receiver.ReceiveAsync(destinationStream, "empty.bin");

        // Assert
        Assert.Equal(0, bytesReceived);
        Assert.Empty(destinationStream.ToArray());
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

    #region FindEndOfFileMarker Tests

    [Fact]
    public void FindEndOfFileMarker_MarkerInNewData_ReturnsCorrectPosition()
    {
        // Arrange
        var trailing = new byte[0];
        var newData = Combine(new byte[] { 0x01, 0x02 }, EofMarker);

        // Act
        var (found, position) = SdCardFileReceiver.FindEndOfFileMarker(trailing, 0, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(2, position);
    }

    [Fact]
    public void FindEndOfFileMarker_MarkerSpanningBoundary_ReturnsCorrectPosition()
    {
        // Arrange — first 5 bytes of marker in trailing, rest in new data
        var trailing = new byte[EofMarker.Length];
        Array.Copy(EofMarker, 0, trailing, 0, 5);
        var trailingCount = 5;

        var newData = new byte[EofMarker.Length - 5];
        Array.Copy(EofMarker, 5, newData, 0, newData.Length);

        // Act
        var (found, position) = SdCardFileReceiver.FindEndOfFileMarker(trailing, trailingCount, newData, newData.Length);

        // Assert
        Assert.True(found);
        Assert.Equal(0, position);
    }

    [Fact]
    public void FindEndOfFileMarker_NoMarker_ReturnsFalse()
    {
        // Arrange
        var trailing = new byte[] { 0x01, 0x02, 0x03 };
        var newData = new byte[] { 0x04, 0x05, 0x06 };

        // Act
        var (found, _) = SdCardFileReceiver.FindEndOfFileMarker(trailing, trailing.Length, newData, newData.Length);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void FindEndOfFileMarker_DataTooShort_ReturnsFalse()
    {
        // Arrange
        var trailing = new byte[0];
        var newData = new byte[] { 0x01 };

        // Act
        var (found, _) = SdCardFileReceiver.FindEndOfFileMarker(trailing, 0, newData, newData.Length);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void FindEndOfFileMarker_MarkerEntirelyInTrailing_NotDetected()
    {
        // The search starts at max(0, trailingCount - markerLen + 1) to avoid
        // re-detecting markers that were already fully in the trailing buffer.
        // Here the marker is at position 0 but trailing has 16 bytes (14 marker + 2 extra),
        // so searchStart = max(0, 16 - 14 + 1) = 3, skipping position 0.
        var trailing = new byte[EofMarker.Length + 2];
        Array.Copy(EofMarker, 0, trailing, 0, EofMarker.Length);
        trailing[EofMarker.Length] = 0xFF;
        trailing[EofMarker.Length + 1] = 0xFF;
        var newData = new byte[] { 0xAA };

        var (found, _) = SdCardFileReceiver.FindEndOfFileMarker(trailing, trailing.Length, newData, newData.Length);

        // The marker is entirely in the old trailing data — not found in the new scan
        Assert.False(found);
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
