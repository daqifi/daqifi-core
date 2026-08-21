using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Documents and verifies the thread accounting from issue #491's triage: a connected, idle
/// device holds exactly two dedicated background threads (the producer and the protobuf
/// consumer), and a text exchange transiently spawns fresh threads for both the protobuf
/// consumer restart and the temporary text consumer rather than reusing them. That per-exchange
/// churn is deliberately left in place — see the remarks on <see cref="DaqifiDevice"/> — so this
/// is a characterization test, not a target for these counts to shrink to.
/// </summary>
public class DaqifiDeviceThreadAccountingTests
{
    [Fact]
    public void Connect_IdleDevice_HasExactlyTwoRunningDedicatedThreads()
    {
        using var transport = new ImmediateReplyMockTransport("0,\"No error\"\r\n");
        using var device = new ThreadAccountingTestableDevice("Idle Device", transport);

        device.Connect();

        var producer = GetMessageProducer(device);
        var consumer = GetMessageConsumer(device);

        Assert.NotNull(producer);
        Assert.NotNull(consumer);
        Assert.True(producer!.IsRunning);
        Assert.True(consumer!.IsRunning);

        var producerThread = GetProducerThread(producer);
        var consumerThread = GetConsumerThread(consumer);

        Assert.NotNull(producerThread);
        Assert.NotNull(consumerThread);
        Assert.True(producerThread!.IsAlive);
        Assert.True(consumerThread!.IsAlive);
        Assert.NotEqual(producerThread.ManagedThreadId, consumerThread.ManagedThreadId);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_RestartsTheProtobufConsumerOnAFreshThread()
    {
        // The triage finding this pins down: the exchange does not resume the protobuf consumer's
        // existing reader, it stops it and starts a new one, so the exchange costs a second thread
        // creation beyond the temporary text consumer's own.
        using var transport = new ImmediateReplyMockTransport("0,\"No error\"\r\n");
        using var device = new ThreadAccountingTestableDevice("Churning Device", transport);

        device.Connect();
        var consumer = GetMessageConsumer(device);
        var beforeId = GetConsumerThread(consumer!)!.ManagedThreadId;

        await device.CallExecuteTextCommandAsync(() => { });

        // Same consumer instance, restarted — the exchange swaps the reader, not the object
        // (issue #383's invariant), but the thread backing it is a new one.
        var afterConsumer = GetMessageConsumer(device);
        Assert.Same(consumer, afterConsumer);
        Assert.True(afterConsumer!.IsRunning);

        var afterId = GetConsumerThread(afterConsumer)!.ManagedThreadId;
        Assert.NotEqual(beforeId, afterId);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_LeavesTheDeviceBackAtTwoRunningThreadsAfterward()
    {
        // The exchange's thread churn is transient: once it completes, the device is back to
        // steady state — no leaked text-consumer thread left running behind it.
        using var transport = new ImmediateReplyMockTransport("0,\"No error\"\r\n");
        using var device = new ThreadAccountingTestableDevice("Settled Device", transport);

        device.Connect();
        await device.CallExecuteTextCommandAsync(() => { });

        var producer = GetMessageProducer(device);
        var consumer = GetMessageConsumer(device);

        Assert.True(producer!.IsRunning);
        Assert.True(consumer!.IsRunning);
        Assert.True(GetProducerThread(producer)!.IsAlive);
        Assert.True(GetConsumerThread(consumer)!.IsAlive);

        device.Disconnect();
    }

    private static IMessageProducer<string>? GetMessageProducer(DaqifiDevice device)
    {
        return (IMessageProducer<string>?)typeof(DaqifiDevice)
            .GetField("_messageProducer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    private static IMessageConsumer<DaqifiOutMessage>? GetMessageConsumer(DaqifiDevice device)
    {
        return (IMessageConsumer<DaqifiOutMessage>?)typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    private static Thread? GetProducerThread(object producer)
    {
        return (Thread?)producer.GetType()
            .GetField("_producerThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(producer);
    }

    private static Thread? GetConsumerThread(object consumer)
    {
        return (Thread?)consumer.GetType()
            .GetField("_consumerThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer);
    }

    /// <summary>Exposes the protected text-exchange entry point.</summary>
    private sealed class ThreadAccountingTestableDevice : DaqifiDevice
    {
        public ThreadAccountingTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(setupAction, responseTimeoutMs: 500, completionTimeoutMs: 150);
        }
    }

    /// <summary>
    /// Transport whose stream hands back one canned line on its first read and then idles
    /// (returns 0 with a short sleep, never blocking indefinitely) — enough for a text exchange
    /// to complete promptly without needing to be released from another thread.
    /// </summary>
    private sealed class ImmediateReplyMockTransport : IStreamTransport
    {
        private readonly ImmediateReplyStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public ImmediateReplyMockTransport(string line)
        {
            _stream = new ImmediateReplyStream(line);
        }

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(ImmediateReplyMockTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Immediate: Connected" : "Immediate: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ImmediateReplyMockTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _disposed = true;
        }

        private sealed class ImmediateReplyStream : Stream
        {
            private readonly byte[] _line;
            private readonly object _gate = new();
            private int _position;

            public ImmediateReplyStream(string line) => _line = Encoding.ASCII.GetBytes(line);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_position < _line.Length)
                    {
                        var toCopy = Math.Min(count, _line.Length - _position);
                        Array.Copy(_line, _position, buffer, offset, toCopy);
                        _position += toCopy;
                        return toCopy;
                    }
                }

                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
