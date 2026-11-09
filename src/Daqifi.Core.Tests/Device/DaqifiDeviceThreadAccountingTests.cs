using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Documents and verifies the thread accounting from issue #491's triage: a connected, idle
/// device holds exactly two dedicated background threads (the producer and the reader), and —
/// since issue #900 — a text exchange adds none. It used to stop the reader, start a temporary
/// text reader, and restart the first on a fresh thread; the reader now frames the reply itself.
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
    public async Task ExecuteTextCommand_KeepsTheReaderOnTheSameThread()
    {
        // The finding #900 removes: the exchange used to stop the reader and start a new one,
        // costing a thread creation (and the stop's join) every time. Compared by Thread
        // instance rather than ManagedThreadId, so a reused id cannot make this pass for the
        // wrong reason.
        using var transport = new ImmediateReplyMockTransport("0,\"No error\"\r\n");
        using var device = new ThreadAccountingTestableDevice("Steady Device", transport);

        device.Connect();
        var consumer = GetMessageConsumer(device);
        var beforeThread = GetConsumerThread(consumer!)!;

        await device.CallExecuteTextCommandAsync(() => { });
        await device.CallExecuteTextCommandAsync(() => { });

        var afterConsumer = GetMessageConsumer(device);
        Assert.Same(consumer, afterConsumer);
        Assert.True(afterConsumer!.IsRunning);
        Assert.Same(beforeThread, GetConsumerThread(afterConsumer));
        Assert.True(beforeThread.IsAlive);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_PutsNoSecondReaderOnTheStream()
    {
        // Every thread that ever entered the transport's Read is tracked; the producer never
        // reads. Before #900 each exchange added two — a temporary text reader and the
        // restarted protobuf one — and the restart was a step a stuck stream could refuse,
        // leaving no reader at all (#383). Now there is exactly one, for the whole connection.
        using var transport = new ImmediateReplyMockTransport("0,\"No error\"\r\n");
        using var device = new ThreadAccountingTestableDevice("Single Reader Device", transport);

        device.Connect();
        var consumerThread = GetConsumerThread(GetMessageConsumer(device)!)!;

        for (var i = 0; i < 3; i++)
        {
            await device.CallExecuteTextCommandAsync(() => { });
        }

        Assert.True(GetMessageProducer(device)!.IsRunning);
        Assert.True(GetMessageConsumer(device)!.IsRunning);
        Assert.Equal(new[] { consumerThread }, transport.ObservedReaderThreads);

        device.Disconnect();
    }

    private static IMessageProducer<string>? GetMessageProducer(DaqifiDevice device)
    {
        return (IMessageProducer<string>?)typeof(DaqifiDevice)
            .GetField("_messageProducer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    private static IMessageConsumer<WireMessage>? GetMessageConsumer(DaqifiDevice device)
    {
        return (IMessageConsumer<WireMessage>?)typeof(DaqifiDevice)
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

        /// <summary>Every distinct thread observed calling <see cref="Stream.Read"/>, in order.</summary>
        public IReadOnlyList<Thread> ObservedReaderThreads => _stream.ObservedReaderThreads;

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public void Connect()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ImmediateReplyMockTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
        }

        public void Disconnect()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
        }

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
            private readonly List<Thread> _observedReaderThreads = [];
            private int _position;

            public ImmediateReplyStream(string line) => _line = Encoding.ASCII.GetBytes(line);

            public IReadOnlyList<Thread> ObservedReaderThreads
            {
                get
                {
                    lock (_gate)
                    {
                        return _observedReaderThreads.ToArray();
                    }
                }
            }

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
                    if (!_observedReaderThreads.Contains(Thread.CurrentThread))
                    {
                        _observedReaderThreads.Add(Thread.CurrentThread);
                    }

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
