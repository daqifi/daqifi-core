using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Tests.TestSupport;
using Google.Protobuf;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// End-to-end coverage for the device's one reader (issue #900): a device on a scripted wire that
/// answers SCPI in text and <c>SYSInfoPB?</c> in protobuf, the way the firmware does, driven
/// through the real connect, initialization and text exchanges with nothing stubbed above the
/// stream.
/// </summary>
public class TextExchangeSingleReaderTests
{
    private const string Identity = "DAQiFi,Nq1,7E2815916200E898,01-02";

    private static DaqifiOutMessage Status(string pn = "Nq1") => new()
    {
        DevicePn = pn,
        DeviceSn = 9090539562006014104,
        DeviceFwRev = "3.8.0",
        AnalogInPortNum = 2,
        DigitalPortNum = 2,
    };

    [Fact]
    public async Task InitializeAsync_ReadsTheTextRepliesAndTheStatusFrameWithOneReader()
    {
        // Every exchange the connect makes — the error-queue drain, the setup block and its
        // terminator — and the protobuf status in between, all through the reader started at
        // connect, on the one thread it started on.
        using var transport = new WireDeviceTransport();
        using var device = new DaqifiDevice("Wire Device", transport);

        await device.ConnectAsync();
        WaitUntil.That(() => transport.Wire.ReaderThreads.Count == 1, "the reader to make its first read");
        var reader = transport.Wire.ReaderThreads.Single();
        await device.InitializeAsync();

        Assert.Equal(4, device.Channels.Count);
        Assert.Equal("Nq1", device.Metadata.PartNumber);
        Assert.Equal(new[] { reader }, transport.Wire.ReaderThreads);
        Assert.True(reader.IsAlive);

        await device.DisconnectAsync();
    }

    [Fact]
    public async Task TextExchange_OnADeviceThatWasNeverInitialized_GetsItsReply()
    {
        // The reader's lines reach an exchange through the subscription made at connect. A
        // device connected without being initialized still has to answer a text query.
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Uninitialized Device", transport);

        await device.ConnectAsync();
        var lines = await device.QueryAsync("*IDN?");

        Assert.Equal(new[] { Identity }, lines);
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task StatusFrame_SentAfterAnExchange_IsDecoded()
    {
        // The exchange hands the reader back to protobuf on its way out: a status frame the
        // device sends afterwards reaches the device's status handling, with nothing restarted.
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Status Device", transport);
        await device.ConnectAsync();
        await device.InitializeAsync();

        await device.QueryAsync("*IDN?");

        var statuses = new ConcurrentQueue<DaqifiOutMessage>();
        device.StatusMessageReceived += statuses.Enqueue;
        transport.Wire.PushToHost(StatusReplyBytes(Status("after-exchange")));

        WaitUntil.That(
            () => statuses.Any(s => s.DevicePn == "after-exchange"),
            "the status frame sent after the exchange to be decoded");
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task TextExchange_DiscardsAHalfReceivedFrameRatherThanPrefixingTheReplyWithIt()
    {
        // The device was mid-way through a protobuf frame when the exchange opened. That half
        // frame is dropped at the switch to text, so the reply line comes back clean instead of
        // with binary glued onto its front.
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Half Frame Device", transport);
        await device.ConnectAsync();

        var frame = StatusReplyBytes(Status());
        var half = frame[..(frame.Length / 2)];
        Assert.DoesNotContain((byte)'\n', half);
        transport.Wire.PushToHost(half);

        // Read is not enough: the stream counts the bytes as taken before the reader has buffered
        // them. Waiting for the buffer itself is what puts the half frame in front of the switch.
        WaitUntil.That(
            () => transport.Wire.HostHasReadEverything && ReaderBufferedByteCount(device) > 0,
            "the reader to buffer the half frame");

        var lines = await device.QueryAsync("*IDN?");

        Assert.Equal(new[] { Identity }, lines);
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task ConsecutiveExchanges_EachGetOnlyTheirOwnReply()
    {
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Busy Device", transport);
        await device.ConnectAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(new[] { Identity }, await device.QueryAsync("*IDN?"));
            Assert.Equal(new[] { "0,\"No error\"" }, await device.QueryAsync("SYSTem:ERRor?"));
        }

        Assert.Single(transport.Wire.ReaderThreads);
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task TextExchange_LeavesTheReaderFramingProtobufHoweverItEnds()
    {
        // Success, a throwing setup action and a cancellation all go through the capture's
        // finally. A reader left framing text would turn every later status into "lines".
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Unwinding Device", transport);
        await device.ConnectAsync();

        await device.QueryAsync("*IDN?");
        Assert.True(ReaderFramesProtobuf(device));

        var framingTextDuringSetup = new List<bool>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.RunAsync(() =>
        {
            framingTextDuringSetup.Add(!ReaderFramesProtobuf(device));
            throw new InvalidOperationException("setup failed");
        }));
        Assert.True(ReaderFramesProtobuf(device));

        // Cancelled from inside the setup action, so the cancellation lands while the capture is
        // certainly installed — a timer could fire before the exchange got that far.
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.RunAsync(() =>
        {
            framingTextDuringSetup.Add(!ReaderFramesProtobuf(device));
            cts.Cancel();
        }, responseTimeoutMs: 5000, cts.Token));
        Assert.True(ReaderFramesProtobuf(device));
        Assert.Equal(new[] { true, true }, framingTextDuringSetup);

        await device.DisconnectAsync();
    }

    [Fact]
    public async Task TextExchange_ReadFailureDuringTheExchange_IsReportedOnTheDevice()
    {
        // Issue #378's guarantee, which used to need a per-exchange subscription on the temporary
        // text reader: a read failure while an exchange is listening still reaches ErrorOccurred,
        // now because the reader that fails is the device's own.
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Failing Device", transport);
        var errors = new ConcurrentQueue<(DeviceErrorEventArgs Error, bool DuringExchange)>();
        device.ErrorOccurred += (_, e) => errors.Enqueue((e, !ReaderFramesProtobuf(device)));
        await device.ConnectAsync();

        await device.RunAsync(
            () => transport.Wire.FailNextRead(new IOException("cable pulled")),
            responseTimeoutMs: 3000);

        var failure = Assert.Single(errors, e => e.Error.Source == DeviceErrorSource.MessageConsumer && e.Error.Error is IOException);
        Assert.True(failure.DuringExchange, "the read failure was reported, but not while the exchange was listening");
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task Reconnect_FramesBeforeInitializeAsync_AreNotHandledAsTheNewSession()
    {
        // The reader is subscribed at connect now, so the previous session's protocol handler must
        // not survive the disconnect: a status the board sends between a reconnect and its
        // InitializeAsync would otherwise rewrite channels and raise events for a session that has
        // not started. Dropped, as it was when only InitializeAsync subscribed the reader.
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Reconnecting Device", transport);
        await device.ConnectAsync();
        await device.InitializeAsync();

        var statuses = new ConcurrentQueue<DaqifiOutMessage>();
        device.StatusMessageReceived += statuses.Enqueue;

        await device.DisconnectAsync();
        await device.ConnectAsync();
        transport.Wire.PushToHost(StatusReplyBytes(Status("before-initialize")));

        WaitUntil.That(() => transport.Wire.HostHasReadEverything, "the reader to take the status frame");
        WaitUntil.HoldsFor(
            () => statuses.IsEmpty,
            "a status that arrived before InitializeAsync was handled by the previous session's handler",
            TimeSpan.FromMilliseconds(300));

        // The connection itself is fine: text works, and initialization brings frames back.
        Assert.Equal(new[] { Identity }, await device.QueryAsync("*IDN?"));
        await device.InitializeAsync();
        Assert.Contains(statuses, s => s.DevicePn == "Nq1");

        await device.DisconnectAsync();
    }

    [Fact]
    public async Task TextExchange_WaitsForASlowFrameSubscriberBeforeTimingItsStaleWindow()
    {
        // A late reply to an earlier command is already on the wire when the exchange opens, but
        // the reader is still inside a slow subscriber for the last status frame and cannot read
        // it. The stale-line window must start only once the reader is free, so that the late
        // reply is read inside it and dropped; timed from the switch alone, the reply would be
        // read after the window and returned as this exchange's answer (#396).
        using var transport = new WireDeviceTransport();
        using var device = new SingleReaderTestableDevice("Slow Subscriber Device", transport);
        await device.ConnectAsync();
        await device.InitializeAsync();

        using var inSubscriber = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        device.StatusMessageReceived += status =>
        {
            if (status.DevicePn == "slow")
            {
                inSubscriber.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        transport.Wire.PushToHost(StatusReplyBytes(Status("slow")));
        Assert.True(inSubscriber.Wait(TimeSpan.FromSeconds(5)), "the slow subscriber never ran");

        // Queued behind the busy reader: a reply to a command from before this exchange.
        transport.Wire.PushToHost(Encoding.ASCII.GetBytes("LATE REPLY TO AN EARLIER COMMAND\r\n"));

        var exchange = device.QueryAsync("*IDN?");
        await Task.Delay(300);
        release.Set();

        Assert.Equal(new[] { Identity }, await exchange);
        await device.DisconnectAsync();
    }

    private static int ReaderBufferedByteCount(DaqifiDevice device)
    {
        var consumer = (IMessageConsumer<WireMessage>?)typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
        return consumer?.QueuedMessageCount ?? 0;
    }

    private static bool ReaderFramesProtobuf(DaqifiDevice device)
    {
        var parser = (DeviceWireParser?)typeof(DaqifiDevice)
            .GetField("_wireParser", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
        return parser is { Capture: null };
    }

    /// <summary>
    /// What the firmware sends for <c>SYSInfoPB?</c>: a length-delimited frame, then CRLF.
    /// </summary>
    private static byte[] StatusReplyBytes(DaqifiOutMessage status)
    {
        using var buffer = new MemoryStream();
        status.WriteDelimitedTo(buffer);
        buffer.Write("\r\n"u8);
        return buffer.ToArray();
    }

    private sealed class SingleReaderTestableDevice : DaqifiDevice
    {
        public SingleReaderTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> QueryAsync(string command) =>
            ExecuteTextCommandAsync(() => Send(new ScpiMessage(command)), responseTimeoutMs: 1000, completionTimeoutMs: 100);

        public Task<IReadOnlyList<string>> RunAsync(
            Action setupAction,
            int responseTimeoutMs = 300,
            CancellationToken cancellationToken = default) =>
            ExecuteTextCommandAsync(setupAction, responseTimeoutMs, completionTimeoutMs: 100, cancellationToken);
    }

    /// <summary>
    /// A transport over <see cref="WireDeviceStream"/>.
    /// </summary>
    private sealed class WireDeviceTransport : IStreamTransport
    {
        private bool _isConnected;
        private bool _disposed;

        public WireDeviceStream Wire { get; } = new(StatusReplyBytes(Status()));

        public Stream Stream => _disposed ? throw new ObjectDisposedException(nameof(WireDeviceTransport)) : Wire;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => "Wire";

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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            _isConnected = false;
            _disposed = true;
        }
    }

    /// <summary>
    /// The device end of a serial line: answers each command line written to it the way the
    /// firmware does — text for SCPI queries, a protobuf frame for <c>SYSInfoPB?</c>, nothing for
    /// a command — and lets a test push bytes of its own. Reads block briefly and return nothing
    /// when there is nothing to read, like a serial port with a short read timeout.
    /// </summary>
    private sealed class WireDeviceStream : Stream
    {
        private readonly byte[] _statusReply;
        private readonly BlockingCollection<byte[]> _toHost = new();
        private readonly StringBuilder _fromHost = new();
        private readonly List<Thread> _readerThreads = [];
        private byte[]? _partial;
        private int _partialOffset;
        private int _unread;
        private Exception? _failNextRead;

        public WireDeviceStream(byte[] statusReply) => _statusReply = statusReply;

        public IReadOnlyList<Thread> ReaderThreads
        {
            get
            {
                lock (_readerThreads)
                {
                    return _readerThreads.ToArray();
                }
            }
        }

        public bool HostHasReadEverything => Volatile.Read(ref _unread) == 0;

        public void PushToHost(byte[] bytes)
        {
            Interlocked.Add(ref _unread, bytes.Length);
            _toHost.Add(bytes);
        }

        public void FailNextRead(Exception error) => Volatile.Write(ref _failNextRead, error);

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_readerThreads)
            {
                if (!_readerThreads.Contains(Thread.CurrentThread))
                {
                    _readerThreads.Add(Thread.CurrentThread);
                }
            }

            if (Interlocked.Exchange(ref _failNextRead, null) is { } failure)
            {
                throw failure;
            }

            if (_partial == null)
            {
                if (!_toHost.TryTake(out var next, 10))
                {
                    return 0;
                }

                _partial = next;
                _partialOffset = 0;
            }

            var n = Math.Min(count, _partial.Length - _partialOffset);
            Array.Copy(_partial, _partialOffset, buffer, offset, n);
            _partialOffset += n;
            if (_partialOffset == _partial.Length)
            {
                _partial = null;
            }

            Interlocked.Add(ref _unread, -n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_fromHost)
            {
                _fromHost.Append(Encoding.ASCII.GetString(buffer, offset, count));
                int newline;
                while ((newline = _fromHost.ToString().IndexOf('\n')) >= 0)
                {
                    var command = _fromHost.ToString(0, newline).Trim();
                    _fromHost.Remove(0, newline + 1);
                    if (Answer(command) is { } reply)
                    {
                        PushToHost(reply);
                    }
                }
            }
        }

        private byte[]? Answer(string command) => command switch
        {
            "*IDN?" => Encoding.ASCII.GetBytes(Identity + "\r\n"),
            "SYSTem:ERRor?" => Encoding.ASCII.GetBytes("0,\"No error\"\r\n"),
            "SYSTem:SYSInfoPB?" => _statusReply,
            _ => null,
        };

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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
