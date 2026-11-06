using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for the read timeout a consumer swap leaves on the device's stream (issue #900).
/// </summary>
/// <remarks>
/// <para>
/// Every text exchange stops the protobuf consumer, and every stop joins a reader thread that is
/// parked inside a blocking read. So the stream's read timeout is the floor on how long an
/// exchange takes before it has sent anything: on a bench Nq1 that was 483-499 ms, three times
/// over, on every connect. The transports now set a short operational read timeout, and these
/// tests pin the two rules that keep it short where it matters and long where it is needed.
/// </para>
/// <para>
/// The rules are one-way on purpose. A text exchange may only <i>shorten</i> the timeout it
/// found — raising it would re-add exactly the join latency the short value removed. A raw
/// capture may only <i>lengthen</i> it, because its one production caller (the SD file download)
/// reads the stream itself and treats a read that times out with no bytes as "the device went
/// quiet".
/// </para>
/// </remarks>
public class TextExchangeReadTimeoutTests
{
    private const int ResponseTimeoutMs = 1000;
    private const int CompletionTimeoutMs = 100;

    /// <summary>
    /// What <c>responseTimeoutMs / 4</c>, clamped into [100, 500], comes to for
    /// <see cref="ResponseTimeoutMs"/>. The ceiling an exchange asks for when the stream it finds
    /// is slower to stop than that.
    /// </summary>
    private const int ExchangeCeilingMs = ResponseTimeoutMs / 4;

    /// <summary>A read timeout a transport would set today: short enough to stop promptly.</summary>
    private const int PromptStopTimeoutMs = 50;

    [Fact]
    public async Task ExecuteTextCommand_WhenTheStreamAlreadyStopsPromptly_LeavesItsReadTimeoutAlone()
    {
        // The regression this issue is about. The exchange used to assign its own ceiling
        // unconditionally, so a stream the transport had already made quick to stop was pushed
        // back up to 250ms for the duration of the exchange - and the text consumer's own stop at
        // the end of the exchange then had to wait that out.
        using var transport = new TimeoutRecordingTransport(PromptStopTimeoutMs, "0,\"No error\"\r\n");
        using var device = new ReadTimeoutTestableDevice("Prompt Stop Device", transport);

        device.Connect();
        try
        {
            await device.CallAsync(() => transport.Release());
        }
        finally
        {
            device.Disconnect();
        }

        Assert.Empty(transport.ReadTimeoutAssignments);
        Assert.Equal(PromptStopTimeoutMs, transport.CurrentReadTimeout);
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheStreamParksForTooLong_ShortensItsReadTimeoutAndPutsItBack()
    {
        // The other direction, unchanged: a stream that would park a reader for 30s gets a
        // ceiling for the exchange, so the text consumer this exchange starts is still joinable
        // when the exchange ends. The original is restored on the way out.
        const int SlowStopTimeoutMs = 30_000;
        using var transport = new TimeoutRecordingTransport(SlowStopTimeoutMs, "0,\"No error\"\r\n");
        using var device = new ReadTimeoutTestableDevice("Slow Stop Device", transport);

        device.Connect();
        try
        {
            await device.CallAsync(() => transport.Release());
        }
        finally
        {
            device.Disconnect();
        }

        Assert.Equal([ExchangeCeilingMs, SlowStopTimeoutMs], transport.ReadTimeoutAssignments);
        Assert.Equal(SlowStopTimeoutMs, transport.CurrentReadTimeout);
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenTheStreamCannotTimeOut_AsksItForNothing()
    {
        // A stream that reports CanTimeout false is never assigned at all, rather than throwing
        // out of an exchange that was otherwise fine.
        using var transport = new TimeoutRecordingTransport(PromptStopTimeoutMs, "0,\"No error\"\r\n")
        {
            StreamCanTimeout = false
        };
        using var device = new ReadTimeoutTestableDevice("Untimed Device", transport);

        device.Connect();
        try
        {
            await device.CallAsync(() => transport.Release());
        }
        finally
        {
            device.Disconnect();
        }

        Assert.Empty(transport.ReadTimeoutAssignments);
    }

    [Fact]
    public async Task RawCapture_LengthensTheReadTimeoutForTheCaptureAndPutsItBack()
    {
        // The SD download's stall tolerance. It reads the stream itself and reads a timed-out
        // read as silence from the device, so a 50ms operational timeout would abort a transfer
        // over an ordinary gap in the device's reply. The capture asks for the 500ms that
        // tolerance has always been, and gives it back.
        using var transport = new TimeoutRecordingTransport(PromptStopTimeoutMs, string.Empty);
        using var device = new ReadTimeoutTestableDevice("Capture Device", transport);

        device.Connect();
        int insideCapture;
        try
        {
            insideCapture = -1;
            await device.CaptureAsync((_, _) =>
            {
                insideCapture = transport.CurrentReadTimeout;
                return Task.CompletedTask;
            });
        }
        finally
        {
            device.Disconnect();
        }

        Assert.True(
            insideCapture >= 500,
            $"A raw capture must read with a tolerant timeout, but the stream was left at {insideCapture}ms.");
        Assert.Equal(PromptStopTimeoutMs, transport.CurrentReadTimeout);
    }

    [Fact]
    public async Task RawCapture_WhenTheStreamIsAlreadyTolerant_LeavesItsReadTimeoutAlone()
    {
        // One-way, like the exchange's rule: a stream already slower than the capture needs is
        // not shortened to 500ms by a capture that only ever wanted a floor.
        const int TolerantTimeoutMs = 20_000;
        using var transport = new TimeoutRecordingTransport(TolerantTimeoutMs, string.Empty);
        using var device = new ReadTimeoutTestableDevice("Tolerant Device", transport);

        device.Connect();
        try
        {
            await device.CaptureAsync((_, _) => Task.CompletedTask);
        }
        finally
        {
            device.Disconnect();
        }

        Assert.Empty(transport.ReadTimeoutAssignments);
        Assert.Equal(TolerantTimeoutMs, transport.CurrentReadTimeout);
    }

    private sealed class ReadTimeoutTestableDevice : DaqifiDevice
    {
        public ReadTimeoutTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: ResponseTimeoutMs,
                completionTimeoutMs: CompletionTimeoutMs);
        }

        public Task CaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
            => ExecuteRawCaptureAsync(rawAction);
    }

    /// <summary>
    /// Transport whose stream can time out, records every read timeout assigned to it, and
    /// withholds one canned reply until the setup action releases it.
    /// </summary>
    private sealed class TimeoutRecordingTransport : IStreamTransport
    {
        private readonly RecordingStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public TimeoutRecordingTransport(int initialReadTimeoutMs, string reply)
        {
            _stream = new RecordingStream(initialReadTimeoutMs, reply);
        }

        /// <summary>Whether the stream admits to supporting timeouts at all.</summary>
        public bool StreamCanTimeout
        {
            get => _stream.TimeoutsSupported;
            init => _stream.TimeoutsSupported = value;
        }

        /// <summary>Every value assigned to the stream's read timeout, in order.</summary>
        public IReadOnlyList<int> ReadTimeoutAssignments => _stream.Assignments;

        /// <summary>The stream's read timeout right now.</summary>
        public int CurrentReadTimeout => _stream.CurrentReadTimeout;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(TimeoutRecordingTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Recording: Connected" : "Recording: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Lets the canned reply reach the stream, as a device answering would.</summary>
        public void Release() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TimeoutRecordingTransport));
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
            private readonly byte[] _reply;
            private readonly object _gate = new();
            private readonly List<int> _assignments = [];
            private int _readTimeout;
            private bool _released;
            private int _position;

            public RecordingStream(int initialReadTimeoutMs, string reply)
            {
                _readTimeout = initialReadTimeoutMs;
                _reply = Encoding.ASCII.GetBytes(reply);
            }

            public bool TimeoutsSupported { get; set; } = true;

            public IReadOnlyList<int> Assignments
            {
                get
                {
                    lock (_gate)
                    {
                        return _assignments.ToArray();
                    }
                }
            }

            public int CurrentReadTimeout
            {
                get
                {
                    lock (_gate)
                    {
                        return _readTimeout;
                    }
                }
            }

            public void Release()
            {
                lock (_gate)
                {
                    _released = true;
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override bool CanTimeout => TimeoutsSupported;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int ReadTimeout
            {
                get
                {
                    if (!TimeoutsSupported) throw new InvalidOperationException("Timeouts are not supported.");
                    lock (_gate)
                    {
                        return _readTimeout;
                    }
                }
                set
                {
                    if (!TimeoutsSupported) throw new InvalidOperationException("Timeouts are not supported.");
                    lock (_gate)
                    {
                        _assignments.Add(value);
                        _readTimeout = value;
                    }
                }
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_released && _position < _reply.Length)
                    {
                        var toCopy = Math.Min(count, _reply.Length - _position);
                        Array.Copy(_reply, _position, buffer, offset, toCopy);
                        _position += toCopy;
                        return toCopy;
                    }
                }

                // Idle link. Parked for a tick rather than spun, like a real read waiting out its
                // timeout — short, because these tests are about which timeout is set, not how
                // long a read honours it.
                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
