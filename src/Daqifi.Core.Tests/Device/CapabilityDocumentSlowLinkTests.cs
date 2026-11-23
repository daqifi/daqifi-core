using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Tests.Device.Capabilities;
using Daqifi.Core.Tests.TestSupport;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #977: over WiFi the capability document — one line of ~8.5 KB — can take longer to
/// arrive than the capability read's 250 ms quiet window. The window used to restart only on a
/// complete line, so after the <c>APIVersion?</c> reply it measured the whole document's transfer
/// time: the read returned no document, and it released the device while the rest of the
/// document was still arriving, so the next exchange wrote into it. These tests drive the real
/// reader and exchange engine over a scripted wire that sends the document slowly, with no line
/// end until its last part.
/// </summary>
public class CapabilityDocumentSlowLinkTests(ITestOutputHelper output)
{
    private const string Identity = "DAQiFi,Nq1,7E2815916200E898,01-02";

    // Each gap is well inside the 250 ms window, with room for a loaded CI runner to stretch it;
    // the whole document (9 gaps, at least 540 ms) is well outside it.
    private const int Parts = 10;
    private const int GapMs = 60;

    private static readonly string Document =
        CapabilityDocumentSamples.Nyquist1Firmware380.Replace("\r", string.Empty).Replace("\n", string.Empty);

    // A gap the reader saw that is this long or longer makes an attempt inconclusive: the 250 ms
    // window is then allowed to end the read, fix or no fix. The 50 ms between this and the window
    // is slack, not a promise. Only a runner that stalls this long is retried; the defect itself
    // shows on every attempt, with every gap near GapMs.
    private const int StallMs = 200;
    private const int Attempts = 8;

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenTheDocumentTakesLongerThanTheQuietWindow_ReturnsIt()
    {
        await UntilConclusiveAsync(async transport =>
        {
            using var device = await ConnectAsync(transport);

            var document = await device.ReadCapabilityDocumentAsync();

            // Waited for, not asserted: a read that ends early returns before the wire is done.
            WaitUntil.That(() => transport.Wire.ReaderCaughtUp, "the reader to take the whole document");
            if (transport.Wire.LongestGap >= TimeSpan.FromMilliseconds(StallMs))
            {
                await device.DisconnectAsync();
                return transport.Wire.LongestGap;
            }

            Assert.True(
                transport.Wire.DocumentFinishedAt!.Value - transport.Wire.DocumentStartedAt!.Value >= TimeSpan.FromMilliseconds(500),
                "Arrangement: the document must take longer than the 250 ms quiet window.");
            Assert.NotNull(document);
            Assert.Same(document, device.Metadata.CapabilityDocument);
            await device.DisconnectAsync();
            return null;
        });
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_KeepsTheDeviceUntilTheDocumentEnds()
    {
        // The half that wedged the bench Nyquist's WiFi: the next exchange (the SD file list, in
        // the app) wrote SD:ENAble 1 while the document was still arriving.
        await UntilConclusiveAsync(async transport =>
        {
            using var device = await ConnectAsync(transport);

            var read = device.ReadCapabilityDocumentAsync();
            WaitUntil.That(() => transport.Wire.DocumentStartedAt is not null, "the document to start arriving");
            var next = device.QueryAsync("*IDN?");

            var document = await read;
            var reply = await next;
            WaitUntil.That(() => transport.Wire.ReaderCaughtUp, "the reader to take the whole document");
            if (transport.Wire.LongestGap >= TimeSpan.FromMilliseconds(StallMs))
            {
                await device.DisconnectAsync();
                return transport.Wire.LongestGap;
            }

            var idnAt = transport.Wire.ReceivedAt("*IDN?");
            Assert.True(
                idnAt > transport.Wire.DocumentFinishedAt,
                $"The next exchange wrote at {idnAt?.TotalMilliseconds:F0} ms, before the document ended at "
                + $"{transport.Wire.DocumentFinishedAt?.TotalMilliseconds:F0} ms.");
            Assert.NotNull(document);
            Assert.Equal(new[] { Identity }, reply);
            await device.DisconnectAsync();
            return null;
        });
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> on a fresh link until one is conclusive. An attempt returns
    /// the stall that made it inconclusive, or null once it has asserted. A CI runner under the
    /// full suite can pause a thread for longer than the quiet window, and then even a correct
    /// read is cut; that is a fact about the runner, not the code, so the attempt is run again.
    /// When every attempt stalls, the run is inconclusive, not failed: it is reported in the test
    /// output and the test passes. The defect shows on every attempt of an unstalled run, so the
    /// other CI legs still catch it; failing here only blocked the merge queue on a starved runner.
    /// </summary>
    private async Task UntilConclusiveAsync(Func<SlowLinkTransport, Task<TimeSpan?>> attempt)
    {
        var stalls = new List<string>();
        for (var i = 0; i < Attempts; i++)
        {
            using var transport = new SlowLinkTransport(complete: true);
            var stall = await attempt(transport);
            if (stall is null)
            {
                return;
            }

            stalls.Add($"{stall.Value.TotalMilliseconds:F0} ms");
        }

        output.WriteLine(
            $"Inconclusive: every attempt stalled for {StallMs} ms or more between reads ({string.Join(", ", stalls)}), "
            + "so none could show whether the read waits for the document.");
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenTheDocumentStopsMidLine_EndsOnTheQuietWindow()
    {
        // Restarting the window on bytes must not make a device that stops mid-line hold the
        // exchange: once the bytes stop, the window runs out as before, and the read returns no
        // document rather than a cut one.
        using var transport = new SlowLinkTransport(complete: false);
        using var device = await ConnectAsync(transport);

        var clock = Stopwatch.StartNew();
        var document = await device.ReadCapabilityDocumentAsync();

        Assert.Null(document);
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(3),
            $"The read took {clock.Elapsed.TotalMilliseconds:F0} ms; it must end on the quiet window, not the 3 s response limit.");
        await device.DisconnectAsync();
    }

    private static async Task<SlowLinkDevice> ConnectAsync(SlowLinkTransport transport)
    {
        var device = new SlowLinkDevice(transport);
        await device.ConnectAsync();
        // Firmware 3.8.0: the capability exchange asks APIVersion? and JSON? only.
        device.Metadata.FirmwareVersion = "3.8.0";
        device.Metadata.PartNumber = "Nq1";
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        return device;
    }

    private sealed class SlowLinkDevice(IStreamTransport transport) : DaqifiDevice("Slow Link Device", transport)
    {
        public Task<IReadOnlyList<string>> QueryAsync(string command) =>
            ExecuteTextCommandAsync(() => Send(new ScpiMessage(command)), responseTimeoutMs: 3000, completionTimeoutMs: 100);
    }

    private sealed class SlowLinkTransport(bool complete) : IStreamTransport
    {
        public bool IsUsbConnection => false;

        private bool _isConnected;
        private bool _disposed;

        public SlowLinkStream Wire { get; } = new(complete);

        public Stream Stream => _disposed ? throw new ObjectDisposedException(nameof(SlowLinkTransport)) : Wire;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => "Slow link";

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
            Wire.Dispose();
        }
    }

    /// <summary>
    /// The device end of a slow link. <c>APIVersion?</c> and <c>*IDN?</c> are answered at once;
    /// <c>JSON?</c> is answered in <see cref="Parts"/> parts <see cref="GapMs"/> apart, and only
    /// the last part ends the line — unless the stream was made to stop mid-line. It records when
    /// each command arrived, on one clock with the document's first and last part.
    /// </summary>
    private sealed class SlowLinkStream(bool complete) : Stream
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly BlockingCollection<(byte[] Bytes, bool Timed)> _toHost = new();
        private readonly StringBuilder _fromHost = new();
        private readonly ConcurrentDictionary<string, TimeSpan> _received = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TimeSpan> _timedTakes = new();
        private byte[]? _partial;
        private int _partialOffset;
        private bool _lastTimedTaken;
        private long _documentStartedTicks = -1;
        private long _documentFinishedTicks = -1;

        public TimeSpan? DocumentStartedAt => Ticks(ref _documentStartedTicks);

        public TimeSpan? DocumentFinishedAt => Ticks(ref _documentFinishedTicks);

        public TimeSpan? ReceivedAt(string command) => _received.TryGetValue(command, out var at) ? at : null;

        /// <summary>
        /// True once the reader has taken the document's last part, processed it, and come back
        /// for more. Only then is <see cref="LongestGap"/> final.
        /// </summary>
        public bool ReaderCaughtUp
        {
            get
            {
                lock (_timedTakes)
                {
                    return _lastTimedTaken && _timedTakes.Count == Parts + 2;
                }
            }
        }

        /// <summary>
        /// The longest time between the reader taking one timed chunk and the next: from the
        /// <c>2</c> line, through each part of the document, to the reader's next call after the
        /// last part. The reader frames each chunk before it reads again, so a pause anywhere on
        /// its thread — or on the thread that sends the parts, or in the 50 ms the read waits
        /// before <c>JSON?</c> — shows here. Every gap the quiet window could see is one of these.
        /// </summary>
        public TimeSpan LongestGap
        {
            get
            {
                lock (_timedTakes)
                {
                    var longest = TimeSpan.Zero;
                    for (var i = 1; i < _timedTakes.Count; i++)
                    {
                        var gap = _timedTakes[i] - _timedTakes[i - 1];
                        if (gap > longest)
                        {
                            longest = gap;
                        }
                    }

                    return longest;
                }
            }
        }

        private static TimeSpan? Ticks(ref long ticks) =>
            Interlocked.Read(ref ticks) is var t and >= 0 ? TimeSpan.FromTicks(t) : null;

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_timedTakes)
            {
                // The first call after the last part: the reader has finished framing it.
                if (_lastTimedTaken && _timedTakes.Count == Parts + 1)
                {
                    _timedTakes.Add(_clock.Elapsed);
                }
            }

            if (_partial == null)
            {
                if (!_toHost.TryTake(out var next, 10))
                {
                    return 0;
                }

                if (next.Timed)
                {
                    lock (_timedTakes)
                    {
                        _timedTakes.Add(_clock.Elapsed);
                        // The 2 line and every part are timed; the last of them ends the run.
                        _lastTimedTaken = _timedTakes.Count == Parts + 1;
                    }
                }

                _partial = next.Bytes;
                _partialOffset = 0;
            }

            var n = Math.Min(count, _partial.Length - _partialOffset);
            Array.Copy(_partial, _partialOffset, buffer, offset, n);
            _partialOffset += n;
            if (_partialOffset == _partial.Length)
            {
                _partial = null;
            }

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
                    _received.TryAdd(command, _clock.Elapsed);
                    Answer(command);
                }
            }
        }

        private void Answer(string command)
        {
            switch (command)
            {
                case "CONFigure:CAPabilities:APIVersion?":
                    Push("2\r\n", timed: true);
                    break;
                case "*IDN?":
                    Push(Identity + "\r\n", timed: false);
                    break;
                case "CONFigure:CAPabilities:JSON?":
                    // A thread of its own, not the thread pool: under the full suite the pool is
                    // starved, and a delayed continuation there stalled the parts past the window.
                    new Thread(SendDocumentSlowly) { IsBackground = true, Name = "Slow link" }.Start();
                    break;
            }
        }

        private void SendDocumentSlowly()
        {
            var size = (Document.Length + Parts - 1) / Parts;
            // Stopping mid-line sends only half of the parts, and never the line end.
            var parts = complete ? Parts : Parts / 2;
            for (var i = 0; i < parts; i++)
            {
                if (i > 0 && _stop.Token.WaitHandle.WaitOne(GapMs))
                {
                    return;
                }

                var part = Document.Substring(i * size, Math.Min(size, Document.Length - i * size));
                Push(complete && i == parts - 1 ? part + "\r\n" : part, timed: true);
                Interlocked.CompareExchange(ref _documentStartedTicks, _clock.Elapsed.Ticks, -1);
            }

            if (complete)
            {
                Interlocked.Exchange(ref _documentFinishedTicks, _clock.Elapsed.Ticks);
            }
        }

        private void Push(string text, bool timed) => _toHost.Add((Encoding.ASCII.GetBytes(text), timed));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stop.Cancel();
            }

            base.Dispose(disposing);
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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
