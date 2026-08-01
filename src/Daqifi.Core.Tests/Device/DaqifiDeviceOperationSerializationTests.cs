using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for per-device operation serialization (#342).
/// </summary>
/// <remarks>
/// The contract under test: individual calls are safe from any thread; a sequence that must not be
/// split goes in <see cref="DaqifiDevice.RunExclusiveAsync{TResult}"/>; a <see cref="DaqifiDevice.Send{T}"/>
/// from another thread is deferred rather than blocked while one runs; and nothing here can
/// deadlock against the text-exchange or lifecycle locks that were already in place.
/// </remarks>
public class DaqifiDeviceOperationSerializationTests
{
    private static readonly TimeSpan DeadlockBudget = TimeSpan.FromSeconds(15);

    // ── Mutual exclusion ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_NeverRunsTwoOperationsAtOnce()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Exclusive Device", transport);
        device.Connect();

        var overlapped = false;
        var inFlight = 0;

        var operations = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            device.RunExclusiveAsync(async _ =>
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    Volatile.Write(ref overlapped, true);
                }

                await Task.Delay(25);
                Interlocked.Decrement(ref inFlight);
            })));

        await Task.WhenAll(operations).WaitAsync(DeadlockBudget);

        Assert.False(Volatile.Read(ref overlapped), "Two exclusive operations ran at the same time.");

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_ReleasesTheLockWhenTheBodyThrows()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Throwing Device", transport);
        device.Connect();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.RunExclusiveAsync(_ => throw new InvalidOperationException("boom")));

        // A leaked lock would hang here instead of completing.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_WhenDisposed_ThrowsDeviceNotConnected()
    {
        var transport = new RecordingTransport();
        var device = new DaqifiDevice("Disposed Device", transport);
        device.Connect();
        device.Dispose();

        var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.RunExclusiveAsync(_ => Task.CompletedTask));

        Assert.True(ex.IsShuttingDown);
    }

    // ── Reentrancy / deadlock guards ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_IsReentrantOnTheSameFlow()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Reentrant Device", transport);
        device.Connect();

        var reached = false;

        await device.RunExclusiveAsync(async ct =>
        {
            await device.RunExclusiveAsync(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            }, ct);
        }).WaitAsync(DeadlockBudget);

        Assert.True(reached);

        device.Disconnect();
    }

    [Fact]
    public async Task RunExclusiveAsync_AllowsANestedTextExchange()
    {
        // The deadlock this guards: text queries (SD listings, diagnostics, the capability
        // document) take the same lock RunExclusiveAsync holds. A non-reentrant acquisition here
        // would hang forever on a lock this very flow is holding.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Nesting Device", transport);
        device.Connect();

        var lines = await device.RunExclusiveAsync(
            _ => device.RunTextExchangeAsync(() => device.Send(ScpiMessageProducer.GetDeviceInfo)))
            .WaitAsync(DeadlockBudget);

        Assert.NotNull(lines);
        Assert.Contains(transport.Writes, w => w.Contains("SYSInfoPB", StringComparison.Ordinal));

        device.Disconnect();
    }

    [Fact]
    public async Task Disconnect_FromInsideAnExclusiveOperation_DoesNotStall()
    {
        // Teardown waits for the operation lock before ripping the transport away. From inside an
        // exclusive operation that is the caller's own lock, so it must run nested instead of
        // burning the whole 10s courtesy budget waiting on itself.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Self-disconnecting Device", transport);
        device.Connect();

        var sw = Stopwatch.StartNew();
        await device.RunExclusiveAsync(_ =>
        {
            device.Disconnect();
            return Task.CompletedTask;
        }).WaitAsync(DeadlockBudget);
        sw.Stop();

        Assert.False(device.IsConnected);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Disconnect from inside an exclusive operation took {sw.Elapsed.TotalSeconds:0.#}s; it waited on its own lock.");
    }

    [Fact]
    public async Task Disconnect_FromAnotherFlow_StillTearsDownWhileAnOperationIsInFlight()
    {
        // Teardown must never be blocked indefinitely by an operation. The cancellation token
        // shortens the courtesy wait, which is the same exit the 10s timeout takes.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Torn-down Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        using var shortWait = new CancellationTokenSource();
        await shortWait.CancelAsync();
        await device.DisconnectAsync(shortWait.Token).WaitAsync(DeadlockBudget);

        Assert.False(device.IsConnected);

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);
    }

    // ── Send deferral ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_FromAnotherFlow_IsHeldBackUntilTheOperationFinishes()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Deferring Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));

        // Long enough that the producer thread would have written it had it been queued.
        await Task.Delay(250);
        Assert.DoesNotContain(transport.Writes, w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "DIO:PORt:STATe");
    }

    [Fact]
    public async Task Send_FromAnotherFlow_DoesNotBlockWhileAnOperationIsInFlight()
    {
        // Deferred, not blocked: Send has always been fire-and-forget and must keep returning
        // immediately even when the device is owned by someone else.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Non-blocking Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        var sw = Stopwatch.StartNew();
        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Send blocked for {sw.Elapsed.TotalSeconds:0.##}s while another flow owned the device.");

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);
    }

    [Fact]
    public async Task Send_FromTheOwningFlow_GoesStraightOut()
    {
        // The operation's own commands must not be parked — the operation would be waiting on
        // itself to finish before its own commands could be sent.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Owning Device", transport);
        device.Connect();

        await device.RunExclusiveAsync(async _ =>
        {
            device.Send(ScpiMessageProducer.SetDioPortState(4, 1));
            await WaitForWriteAsync(transport, "DIO:PORt:STATe");
        }).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task Send_DeferredMessagesAreDeliveredInOrder()
    {
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Ordering Device", transport);
        device.Connect();

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() =>
        {
            for (var channel = 1; channel <= 5; channel++)
            {
                device.Send(ScpiMessageProducer.SetDioPortState(channel, 1));
            }
        });

        release.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "DIO:PORt:STATe 5");

        var states = transport.Writes
            .Where(w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, states.Count);
        for (var channel = 1; channel <= 5; channel++)
        {
            Assert.Contains($"STATe {channel}", states[channel - 1], StringComparison.Ordinal);
        }

        device.Disconnect();
    }

    [Fact]
    public async Task Send_FromAnotherFlow_IsHeldBackDuringAPlainTextExchange()
    {
        // The hazard the whole feature exists for: a text query owns the stream with the protobuf
        // consumer stopped, so a command written by another thread has its reply collected as part
        // of that query's answer.
        using var transport = new RecordingTransport();
        using var device = new TextExchangeDevice("Querying Device", transport);
        device.Connect();

        // The sender is a real thread started before the exchange opens, so it carries none of the
        // exchange's execution context. That matters: work started from *inside* the exchange
        // inherits its ownership of the lock and is deliberately not deferred.
        using var sendNow = new ManualResetEventSlim(false);
        using var sent = new ManualResetEventSlim(false);

        var sender = new Thread(() =>
        {
            sendNow.Wait(DeadlockBudget);
            device.Send(ScpiMessageProducer.SetDioPortState(4, 1));
            sent.Set();
        })
        {
            IsBackground = true,
        };
        sender.Start();

        var exchange = device.RunTextExchangeAsync(() => sendNow.Set());

        Assert.True(sent.Wait(DeadlockBudget), "The sending thread never ran.");
        Assert.DoesNotContain(transport.Writes, w => w.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        await exchange.WaitAsync(DeadlockBudget);
        await WaitForWriteAsync(transport, "DIO:PORt:STATe");

        Assert.True(sender.Join(TimeSpan.FromSeconds(5)));

        device.Disconnect();
    }

    [Fact]
    public async Task TextExchange_CancelledWhileTheOutboundQueueDrains_DoesNotResubscribeTheConsumer()
    {
        // The drain added for #342 is the one step before the consumer swap that can throw. If it
        // threw from inside the swap's try/finally, that finally would "restart" a consumer that
        // was never stopped — Start() early-returns, but the inbound handler is subscribed again,
        // and every frame from then on is dispatched twice.
        using var transport = new BlockedWriteTransport();
        using var device = new TextExchangeDevice("Draining Device", transport);
        device.Connect();

        // Queue more than the blocked writer can drain, so the exchange is still draining when the
        // token fires.
        for (var i = 0; i < 5; i++)
        {
            device.Send(ScpiMessageProducer.SetDioPortState(i, 1));
        }

        var before = InboundSubscriberCount(device);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.RunTextExchangeAsync(() => { }, cts.Token));

        // A restart that never should have run adds a subscriber; the count must be untouched.
        Assert.Equal(before, InboundSubscriberCount(device));

        transport.ReleaseWrites();
        device.Disconnect();
    }

    private static int InboundSubscriberCount(DaqifiDevice device)
    {
        var consumer = typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device)!;

        var handler = (Delegate?)consumer.GetType()
            .GetField("MessageReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer);

        return handler?.GetInvocationList().Length ?? 0;
    }

    // ── The inbound path stays clear ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunExclusiveAsync_DoesNotBlockInboundChannelWork()
    {
        // Streaming callbacks, the reader loop and frame decode must never wait on the operation
        // lock — a control operation must not stall a live stream. Channel snapshotting is the
        // device-level state those paths touch.
        using var transport = new RecordingTransport();
        using var device = new DaqifiDevice("Streaming-through Device", transport);
        device.Connect();

        await device.RunExclusiveAsync(async ct =>
        {
            var inbound = Task.Run(() =>
            {
                var seen = 0;
                for (var i = 0; i < 200; i++)
                {
                    seen += device.GetChannelsSnapshot().Count;
                }

                return seen;
            }, ct);

            await inbound.WaitAsync(TimeSpan.FromSeconds(5));
        }).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static async Task WaitForWriteAsync(RecordingTransport transport, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.Writes.Any(w => w.Contains(fragment, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"'{fragment}' never reached the wire. Writes: {string.Join(" | ", transport.Writes)}");
    }

    /// <summary>Exposes the protected text-exchange entry point.</summary>
    private sealed class TextExchangeDevice : DaqifiDevice
    {
        public TextExchangeDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Action setupAction,
            CancellationToken cancellationToken = default) =>
            ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: 300,
                completionTimeoutMs: 100,
                cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Transport whose writes block until released, so the producer's queue stays non-empty and a
    /// text exchange is guaranteed to still be draining it when a cancellation lands.
    /// </summary>
    private sealed class BlockedWriteTransport : IStreamTransport
    {
        private readonly BlockingStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(BlockedWriteTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Blocked: Connected" : "Blocked: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void ReleaseWrites() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BlockedWriteTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _stream.Release();
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed) return;
            _stream.Release();
            _isConnected = false;
            _disposed = true;
        }

        private sealed class BlockingStream : Stream
        {
            private readonly ManualResetEventSlim _released = new(false);

            public void Release() => _released.Set();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(5);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                _released.Wait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Transport over a stream that records every write and never has anything to read, so tests
    /// can assert on exactly what reached the wire and when.
    /// </summary>
    private sealed class RecordingTransport : IStreamTransport
    {
        private readonly RecordingStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public IReadOnlyList<string> Writes => _stream.Writes;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(RecordingTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Recording: Connected" : "Recording: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecordingTransport));
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

        private sealed class RecordingStream : Stream
        {
            private readonly List<string> _writes = new();
            private readonly object _gate = new();

            public IReadOnlyList<string> Writes
            {
                get
                {
                    lock (_gate)
                    {
                        return _writes.ToList();
                    }
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // Nothing to read; back off so the consumer's reader loop doesn't spin.
                Thread.Sleep(5);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    _writes.Add(Encoding.UTF8.GetString(buffer, offset, count));
                }
            }
        }
    }
}
