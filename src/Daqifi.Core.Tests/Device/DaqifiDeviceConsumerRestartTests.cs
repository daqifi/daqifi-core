using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Communication.Messages;
using System.Collections.Concurrent;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawCapture_DetachesTheInboundHandlerAndPutsItBackHoweverTheRestartGoes(bool stuckStream)
    {
        // Detached during the capture, so a reader that outlives its stop cannot hand the captured
        // bytes to the device as live frames; re-attached afterwards even when the restart is
        // refused, because the same subscription carries every later exchange's reply lines.
        using var transport = new StallingReadMockTransport(
            readBlock: stuckStream ? Timeout.InfiniteTimeSpan : TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Capturing Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, InboundSubscriberCount(device));

        var duringCapture = -1;
        await device.CallRawCaptureAsync((_, _) =>
        {
            duringCapture = InboundSubscriberCount(device);
            return Task.CompletedTask;
        });

        Assert.Equal(0, duringCapture);
        Assert.Equal(1, InboundSubscriberCount(device));
        Assert.Equal(!stuckStream, GetMessageConsumer(device)!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_OnAStuckStream_RaisesErrorOccurredForTheReader()
    {
        // Issue #975: the refusal used to reach a log line only, and a caller that gives Core no
        // logger never saw it while the device kept reporting Connected.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);
        var errors = new ConcurrentQueue<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) => errors.Enqueue(e);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        var error = Assert.Single(errors);
        Assert.Equal(DeviceErrorSource.MessageConsumer, error.Source);
        var refusal = Assert.IsType<ConsumerThreadNotExitedException>(error.Error);
        Assert.IsType<ConsumerThreadNotExitedException>(refusal.InnerException);
        Assert.Contains("raw capture", refusal.Message);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_WhenTheReaderRestarts_RaisesNoError()
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Prompt Device", transport);
        var errors = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        Assert.True(GetMessageConsumer(device)!.IsRunning);
        Assert.Equal(0, Volatile.Read(ref errors));

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_AfterARawCaptureLeftTheReaderStuck_ThrowsAndSendsNoStartCommand()
    {
        // Issue #975: a stream start is not a text exchange, so it used to send the start
        // command to a stopped reader and report a running session that delivered nothing.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(GetMessageConsumer(device)!.IsRunning);

        var refusal = Assert.Throws<ConsumerThreadNotExitedException>(() => device.StartStreaming());

        Assert.Contains(nameof(DaqifiStreamingDevice.StartStreaming), refusal.Message);
        Assert.False(device.IsStreaming);
        Assert.DoesNotContain(device.SentCommands, c => c.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase));

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_AfterTheStuckReadReturns_RevivesTheReaderAndSendsTheStart()
    {
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Recovering Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(original!.IsRunning);

        transport.ReleaseReaders();
        device.StartStreaming();

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);
        Assert.True(device.IsStreaming);
        Assert.Contains(device.SentCommands, c => c.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase));

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_DuringARawCaptureOnAnotherFlow_LeavesTheStoppedReaderAlone()
    {
        // The capture stopped the reader on purpose. Starting it now would put a reader on the
        // stream that the capture reads, so the start's command is parked and the reader waits
        // for the capture's own restart.
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Capturing Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        device.StartStreaming();
        var runningDuringCapture = GetMessageConsumer(device)!.IsRunning;

        endCapture.Set();
        await capture;

        Assert.False(runningDuringCapture);
        Assert.True(GetMessageConsumer(device)!.IsRunning);
        Assert.True(device.IsStreaming);
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5)),
            "The parked start must reach the wire once the capture restarts the reader.");

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_DuringARawCaptureWhoseRestartFails_SendsNoStartAndEndsTheSession()
    {
        // Issue #975, the concurrent half. A start on another flow during the capture cannot check
        // the reader, because the capture stopped it on purpose, so the start is parked. When the
        // capture then cannot restart the reader, the parked start must not go out: the session
        // would report running and deliver nothing, and a later start would return early on it.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);
        var errors = new ConcurrentQueue<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) => errors.Enqueue(e);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        device.StartStreaming();
        Assert.True(device.IsStreaming);

        endCapture.Set();
        await capture;

        Assert.False(GetMessageConsumer(device)!.IsRunning);
        Assert.False(device.IsStreaming);

        // Writes still work on a stuck reader. A marker sent now is written after anything that
        // was parked, so once it is on the wire, a start that went out would be there too.
        device.Send(new ScpiMessage("MARKER"));
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("MARKER", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            "The marker must reach the wire.");
        Assert.DoesNotContain("StartStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);

        // The caller hears that the reader is stuck. The dropped start's refusal has the same
        // source and type a moment later, so ErrorOccurred's throttle folds it into this report.
        var error = Assert.Single(errors);
        Assert.Equal(DeviceErrorSource.MessageConsumer, error.Source);
        Assert.IsType<ConsumerThreadNotExitedException>(error.Error);

        // Not an early return on a session that never ran: the next start checks the reader and
        // says why it cannot.
        Assert.Throws<ConsumerThreadNotExitedException>(() => device.StartStreaming());
        Assert.False(device.IsStreaming);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task StartStreamingAsync_DuringARawCaptureWhoseRestartFails_ThrowsTheReaderRefusal()
    {
        // Issue #975, review of #976: the async start awaits its confirmation while the start is
        // parked. When the capture then cannot restart the reader, the parked start is dropped and
        // the session ends; the awaiting call must say so rather than complete as a success.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        var start = Task.Run(() => device.StartStreamingAsync());
        Assert.True(
            SpinWait.SpinUntil(() => device.IsStreaming, TimeSpan.FromSeconds(5)),
            "The start must be parked behind the capture, with the session recorded as live.");

        endCapture.Set();
        await capture;

        var refusal = await Assert.ThrowsAsync<ConsumerThreadNotExitedException>(() => start);
        Assert.Contains(nameof(DaqifiStreamingDevice.StartStreaming), refusal.Message);
        Assert.False(device.IsStreaming);
        Assert.DoesNotContain("StartStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    private static int InboundSubscriberCount(DaqifiDevice device)
    {
        var consumer = GetMessageConsumer(device)!;
        var handler = (Delegate?)consumer.GetType()
            .GetField("MessageParsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer);
        return handler?.GetInvocationList().Length ?? 0;
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
            return ExecuteTextCommandAsync(setupAction, responseTimeout: TimeSpan.FromMilliseconds(100), completionTimeout: TimeSpan.FromMilliseconds(50));
        }

        public Task CallRawCaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
        {
            return ExecuteRawCaptureAsync(rawAction);
        }
    }

    /// <summary>
    /// The streaming counterpart of <see cref="ConsumerRestartTestableDevice"/>: exposes the raw
    /// capture and records every text command the device sends.
    /// </summary>
    private sealed class StreamingConsumerRestartTestableDevice : DaqifiStreamingDevice
    {
        public StreamingConsumerRestartTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public ConcurrentQueue<string> SentCommands { get; } = new();

        public Task CallRawCaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
        {
            return ExecuteRawCaptureAsync(rawAction);
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> text)
            {
                SentCommands.Enqueue(text.Data);
            }

            base.Send(message);
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
        public bool IsUsbConnection => false;

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

        /// <summary>Everything written to the stream so far, as ASCII.</summary>
        public string Written => _stream.Written;

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
            private readonly System.Text.StringBuilder _written = new();

            public string Written
            {
                get
                {
                    lock (_written)
                    {
                        return _written.ToString();
                    }
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_written)
                {
                    _written.Append(System.Text.Encoding.ASCII.GetString(buffer, offset, count));
                }
            }
        }
    }
}
