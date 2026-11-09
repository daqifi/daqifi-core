using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Communication.Messages;
using System.Reflection;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Regression coverage for issue #383 — a connect must not fail with "a previous consumer thread
/// has not yet exited" — and for what replaced the restart that issue was about.
/// </summary>
/// <remarks>
/// A text exchange used to stop the reader and restart it, so a reader parked in a slow read could
/// still be alive at the restart and the double-reader guard rightly refused it. Since issue #900 a
/// text exchange never stops the reader, so on the text path there is nothing to restart. A raw
/// capture still stops and restarts it, and a stream stuck outright still leaves it stopped; what
/// changed is that the next text exchange now says so instead of reporting silence.
/// </remarks>
public class DaqifiDeviceConsumerRestartTests
{
    // Start()'s grace period is covered precisely in StreamMessageConsumerStallingReaderTests.

    [Fact]
    public async Task ExecuteTextCommand_OnAStuckStream_LeavesTheSameReaderRunning()
    {
        // A stream whose read never returns at all. The exchange has nothing to stop, so it
        // neither waits on the stuck read nor replaces the reader parked in it: it hears
        // nothing, says so, and leaves the reader exactly as it found it.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        var lines = await device.CallExecuteTextCommandAsync(() => { });
        Assert.Empty(lines);

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenReaderExitsPromptly_KeepsSameConsumer()
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Prompt Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);

        await device.CallExecuteTextCommandAsync(() => { });

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_OnAStuckStream_LeavesTheReaderStoppedWithoutThrowing()
    {
        // The guard #383 is about, on the one path that still restarts the reader. No
        // replacement may be bound to the stuck stream — it would be a second concurrent reader
        // on it — and the refusal must not escape the capture's finally.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.False(after!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_AfterARawCaptureLeftTheReaderStuck_FailsSayingSo()
    {
        // The reader a raw capture could not restart is the reader every exchange now depends
        // on. Collecting nothing from it and reporting "the device did not answer" would blame
        // the device for a stuck host stream; the exchange asks the reader to start first, and
        // the refusal it gets back is the honest diagnosis.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ConsumerThreadNotExitedException>(
            () => device.CallExecuteTextCommandAsync(() => { }));

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_AfterTheStuckReadReturns_RevivesTheReader()
    {
        // Same stuck capture, but the stuck read returns before the next exchange. The exchange's
        // start then succeeds, and the device has its reader back without a reconnect.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Recovering Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(original!.IsRunning);

        transport.ReleaseReaders();
        await device.CallExecuteTextCommandAsync(() => { });

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        device.Disconnect();
    }

    private static IMessageConsumer<WireMessage>? GetMessageConsumer(DaqifiDevice device)
    {
        return (IMessageConsumer<WireMessage>?)typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    /// <summary>
    /// Exposes the protected text-exchange and raw-capture entry points so they can be driven
    /// directly, without needing a device that answers SCPI.
    /// </summary>
    private class ConsumerRestartTestableDevice : DaqifiDevice
    {
        public ConsumerRestartTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(setupAction, responseTimeoutMs: 100, completionTimeoutMs: 50);
        }

        public Task CallRawCaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
        {
            return ExecuteRawCaptureAsync(rawAction);
        }
    }

    /// <summary>
    /// Transport whose stream models a reader parked in a blocking socket read, mirroring a WiFi
    /// connection whose receive timeout outlasts the reader's stop budget. Writes are accepted
    /// and discarded.
    /// </summary>
    /// <remarks>
    /// <paramref name="readBlock"/> selects the scenario: <see cref="TimeSpan.Zero"/> for a prompt
    /// reader, a finite span for one that outlives the stop budget but does return, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> for a stream that is stuck outright.
    /// </remarks>
    private sealed class StallingReadMockTransport : IStreamTransport
    {
        private readonly StallingStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public StallingReadMockTransport(TimeSpan readBlock)
        {
            _stream = new StallingStream(readBlock);
        }

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(StallingReadMockTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Stalling: Connected" : "Stalling: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public bool WaitForReadEntered(TimeSpan timeout) => _stream.WaitForReadEntered(timeout);

        public void ReleaseReaders() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StallingReadMockTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            _stream.Release();
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _stream.Release();
            _disposed = true;
        }

        private sealed class StallingStream : Stream
        {
            private readonly TimeSpan _readBlock;
            private readonly ManualResetEventSlim _release = new(false);
            private readonly ManualResetEventSlim _readEntered = new(false);

            public StallingStream(TimeSpan readBlock) => _readBlock = readBlock;

            public bool WaitForReadEntered(TimeSpan timeout) => _readEntered.Wait(timeout);

            public void Release() => _release.Set();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _readEntered.Set();
                if (_readBlock == TimeSpan.Zero)
                {
                    Thread.Sleep(10);
                }
                else
                {
                    // Infinite means "stuck stream": only an explicit Release() ever frees it.
                    _release.Wait(_readBlock);
                }

                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
