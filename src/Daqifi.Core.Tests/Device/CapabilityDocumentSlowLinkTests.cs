using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Tests.Device.Capabilities;
using Daqifi.Core.Tests.TestSupport;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

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
public class CapabilityDocumentSlowLinkTests
{
    private const string Identity = "DAQiFi,Nq1,7E2815916200E898,01-02";

    // Each gap is well inside the 250 ms window, with room for a loaded CI runner to stretch it;
    // the whole document (9 gaps, at least 540 ms) is well outside it.
    private const int Parts = 10;
    private const int GapMs = 60;

    private static readonly string Document =
        CapabilityDocumentSamples.Nyquist1Firmware380.Replace("\r", string.Empty).Replace("\n", string.Empty);

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenTheDocumentTakesLongerThanTheQuietWindow_ReturnsIt()
    {
        using var transport = new SlowLinkTransport(complete: true);
        using var device = await ConnectAsync(transport);

        var document = await device.ReadCapabilityDocumentAsync();

        // Waited for, not asserted: a read that ends early returns before the wire is done.
        WaitUntil.That(() => transport.Wire.DocumentFinishedAt is not null, "the wire to finish sending the document");
        Assert.True(
            transport.Wire.DocumentFinishedAt!.Value - transport.Wire.DocumentStartedAt!.Value >= TimeSpan.FromMilliseconds(500),
            "Arrangement: the document must take longer than the 250 ms quiet window.");
        Assert.NotNull(document);
        Assert.Same(document, device.Metadata.CapabilityDocument);
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_KeepsTheDeviceUntilTheDocumentEnds()
    {
        // The half that wedged the bench Nyquist's WiFi: the next exchange (the SD file list, in
        // the app) wrote SD:ENAble 1 while the document was still arriving.
        using var transport = new SlowLinkTransport(complete: true);
        using var device = await ConnectAsync(transport);

        var read = device.ReadCapabilityDocumentAsync();
        WaitUntil.That(() => transport.Wire.DocumentStartedAt is not null, "the document to start arriving");
        var next = device.QueryAsync("*IDN?");

        var document = await read;
        var reply = await next;
        WaitUntil.That(() => transport.Wire.DocumentFinishedAt is not null, "the wire to finish sending the document");

        var idnAt = transport.Wire.ReceivedAt("*IDN?");
        Assert.True(
            idnAt > transport.Wire.DocumentFinishedAt,
            $"The next exchange wrote at {idnAt?.TotalMilliseconds:F0} ms, before the document ended at "
            + $"{transport.Wire.DocumentFinishedAt?.TotalMilliseconds:F0} ms.");
        Assert.NotNull(document);
        Assert.Equal(new[] { Identity }, reply);
        await device.DisconnectAsync();
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
        private readonly BlockingCollection<byte[]> _toHost = new();
        private readonly StringBuilder _fromHost = new();
        private readonly ConcurrentDictionary<string, TimeSpan> _received = new();
        private readonly CancellationTokenSource _stop = new();
        private byte[]? _partial;
        private int _partialOffset;
        private long _documentStartedTicks = -1;
        private long _documentFinishedTicks = -1;

        public TimeSpan? DocumentStartedAt => Ticks(ref _documentStartedTicks);

        public TimeSpan? DocumentFinishedAt => Ticks(ref _documentFinishedTicks);

        public TimeSpan? ReceivedAt(string command) => _received.TryGetValue(command, out var at) ? at : null;

        private static TimeSpan? Ticks(ref long ticks) =>
            Interlocked.Read(ref ticks) is var t and >= 0 ? TimeSpan.FromTicks(t) : null;

        public override int Read(byte[] buffer, int offset, int count)
        {
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
                    Push("2\r\n");
                    break;
                case "*IDN?":
                    Push(Identity + "\r\n");
                    break;
                case "CONFigure:CAPabilities:JSON?":
                    _ = Task.Run(SendDocumentSlowlyAsync);
                    break;
            }
        }

        private async Task SendDocumentSlowlyAsync()
        {
            var size = (Document.Length + Parts - 1) / Parts;
            // Stopping mid-line sends only half of the parts, and never the line end.
            var parts = complete ? Parts : Parts / 2;
            for (var i = 0; i < parts; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(GapMs, _stop.Token).ConfigureAwait(false);
                }

                var part = Document.Substring(i * size, Math.Min(size, Document.Length - i * size));
                Push(complete && i == parts - 1 ? part + "\r\n" : part);
                Interlocked.CompareExchange(ref _documentStartedTicks, _clock.Elapsed.Ticks, -1);
            }

            if (complete)
            {
                Interlocked.Exchange(ref _documentFinishedTicks, _clock.Elapsed.Ticks);
            }
        }

        private void Push(string text) => _toHost.Add(Encoding.ASCII.GetBytes(text));

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
