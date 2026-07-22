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
    [Fact]
    public async Task ExecuteTextCommand_WhenReaderDoesNotExitPromptly_RecoversWithFreshConsumer()
    {
        using var transport = new StallingReadMockTransport();
        using var device = new ConsumerRestartTestableDevice("Stalling Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);

        // Reader is now parked inside Read() for longer than the swap's stop budget.
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        // Pre-fix this threw InvalidOperationException from the restart in the finally block.
        var lines = await device.CallExecuteTextCommandAsync(() => { });
        Assert.Empty(lines);

        // The device recovered by binding a fresh consumer to the transport's current stream,
        // and the stale instance was discarded rather than restarted.
        var replacement = GetMessageConsumer(device);
        Assert.NotNull(replacement);
        Assert.NotSame(original, replacement);
        Assert.True(replacement!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenReaderExitsPromptly_KeepsSameConsumer()
    {
        // The normal path is unchanged: a reader that exits within the stop budget is simply
        // restarted, with no consumer churn.
        using var transport = new StallingReadMockTransport(stallReads: false);
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
    /// Transport whose stream models a reader parked in a blocking socket read: the first read
    /// blocks well past the consumer-swap stop budget, mirroring a WiFi connection whose receive
    /// timeout outlasts the swap. Writes are accepted and discarded.
    /// </summary>
    private sealed class StallingReadMockTransport : IStreamTransport
    {
        private readonly StallingStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public StallingReadMockTransport(bool stallReads = true)
        {
            _stream = new StallingStream(stallReads ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
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
            private readonly TimeSpan _stall;
            private readonly ManualResetEventSlim _release = new(false);
            private readonly ManualResetEventSlim _readEntered = new(false);

            public StallingStream(TimeSpan stall) => _stall = stall;

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
                if (_stall > TimeSpan.Zero)
                {
                    _release.Wait(_stall);
                }
                else
                {
                    Thread.Sleep(10);
                }

                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
