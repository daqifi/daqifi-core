using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Messages;
using System.Reflection;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Regression coverage for issue #383 — a connect must not fail with
/// "a previous consumer thread has not yet exited". The text-exchange path stops the protobuf
/// consumer and restarts it; when the reader is parked in a slow blocking read it can still be
/// alive at restart time, which the consumer's double-reader guard rightly refuses. The device
/// escalates instead of surfacing that as a connect failure.
/// </summary>
public class DaqifiDeviceConsumerRestartTests
{
    // Start()'s grace period is covered precisely in StreamMessageConsumerStallingReaderTests.
    // It is deliberately not asserted from here: the text exchange's own internal delays add well
    // over a second of slack before the restart, so any device-level attempt to isolate the grace
    // would come down to timing tuning and would be flaky in CI.

    [Fact]
    public async Task ExecuteTextCommand_WhenReaderNeverExits_LeavesConsumerStoppedWithoutThrowing()
    {
        // A stream whose read never returns at all. The guard must hold — no replacement consumer
        // may be bound to that stuck stream, since it would be a second concurrent reader on it —
        // but the failure must not surface as an unactionable internal error from a finally block.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        // Completes rather than propagating ConsumerThreadNotExitedException out of the finally.
        var lines = await device.CallExecuteTextCommandAsync(() => { });
        Assert.Empty(lines);

        // Same instance, left stopped — emphatically NOT a fresh consumer racing the stuck reader.
        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.False(after!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenReaderExitsPromptly_KeepsSameConsumer()
    {
        // The normal path is unchanged: a reader that exits within the stop budget is simply
        // restarted, with no grace period needed.
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

    private static IMessageConsumer<DaqifiOutMessage>? GetMessageConsumer(DaqifiDevice device)
    {
        return (IMessageConsumer<DaqifiOutMessage>?)typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    /// <summary>
    /// Exposes the protected text-exchange entry point so the swap can be driven directly,
    /// without needing a device that answers SCPI.
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
    }

    /// <summary>
    /// Transport whose stream models a reader parked in a blocking socket read, mirroring a WiFi
    /// connection whose receive timeout outlasts the consumer-swap stop budget. Writes are accepted
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
