using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for the stale-line boundary in the text exchange (raised while fixing #396).
/// </summary>
/// <remarks>
/// A late reply to an EARLIER command can still be in flight when the next text exchange opens
/// its consumer, and would otherwise be returned as part of the new exchange's response. That is
/// wrong for every caller, but it is actively dangerous for one that infers device liveness from
/// response content: the SD listing accepts a <c>SYSTem:ERRor?</c> reply as proof that the device
/// answered and that the listing before it is complete. A stale line satisfying that check would
/// let a silent device pass as a healthy empty SD card — the exact bug #396 is about.
/// </remarks>
public class DaqifiDeviceStaleTextLineTests
{
    [Fact]
    public async Task ExecuteTextCommand_DropsLinesThatArrivedBeforeTheExchangeSentAnything()
    {
        // The stale line is released into the stream at the moment the exchange binds its text
        // consumer — after the protobuf consumer has been stopped, and before the setup action
        // has sent anything. That is exactly the window a late reply to an earlier command can
        // land in. The device then stays silent, as one that has stopped answering would.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Preloaded Device", transport);

        device.Connect();
        transport.ReleaseOnStreamAccess(2); // 2nd access inside the exchange = text-consumer bind

        var lines = await device.CallExecuteTextCommandAsync(() => { });

        // The exchange sent nothing, so nothing in it can legitimately have been answered.
        Assert.Empty(lines);

        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_KeepsLinesThatArriveAfterTheExchangeSentSomething()
    {
        // The complement, so the fix cannot be "drop everything": a reply that arrives once the
        // setup action has sent its command must still be returned.
        using var transport = new ReleaseOnStreamAccessMockTransport("0,\"No error\"\r\n");
        using var device = new StaleLineTestableDevice("Answering Device", transport);

        device.Connect();

        var lines = await device.CallExecuteTextCommandAsync(() => transport.Release());

        Assert.Contains(lines, l => l.Contains("No error"));

        device.Disconnect();
    }

    /// <summary>Exposes the protected text-exchange entry point.</summary>
    private class StaleLineTestableDevice : DaqifiDevice
    {
        public StaleLineTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(setupAction, responseTimeoutMs: 500, completionTimeoutMs: 150);
        }
    }

    /// <summary>
    /// Transport whose stream withholds one canned line until released, and which can arm that
    /// release on the Nth access of its <see cref="Stream"/> property.
    /// </summary>
    /// <remarks>
    /// Keying off the property access — rather than a delay — makes the timing deterministic:
    /// the text exchange reads <c>Stream</c> once up front and again when it binds the temporary
    /// text consumer, and that second access happens after the protobuf consumer has been stopped
    /// (so it cannot swallow the line first) and before the setup action runs.
    /// </remarks>
    private sealed class ReleaseOnStreamAccessMockTransport : IStreamTransport
    {
        private readonly WithheldLineStream _stream;
        private int _streamAccessCount;
        private int _releaseOnAccess = -1;
        private bool _isConnected;
        private bool _disposed;

        public ReleaseOnStreamAccessMockTransport(string line)
        {
            _stream = new WithheldLineStream(line);
        }

        public Stream Stream
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ReleaseOnStreamAccessMockTransport));

                var access = Interlocked.Increment(ref _streamAccessCount);
                if (_releaseOnAccess > 0 && access == _releaseOnAccess)
                {
                    _stream.Release();
                }

                return _stream;
            }
        }

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Withheld: Connected" : "Withheld: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Arms the release for the Nth subsequent access of <see cref="Stream"/>.</summary>
        public void ReleaseOnStreamAccess(int accessNumber)
        {
            Interlocked.Exchange(ref _streamAccessCount, 0);
            _releaseOnAccess = accessNumber;
        }

        /// <summary>Releases the withheld line immediately.</summary>
        public void Release() => _stream.Release();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReleaseOnStreamAccessMockTransport));
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

        private sealed class WithheldLineStream : Stream
        {
            private readonly byte[] _line;
            private readonly object _gate = new();
            private bool _released;
            private int _position;

            public WithheldLineStream(string line) => _line = Encoding.ASCII.GetBytes(line);

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
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_released && _position < _line.Length)
                    {
                        var toCopy = Math.Min(count, _line.Length - _position);
                        Array.Copy(_line, _position, buffer, offset, toCopy);
                        _position += toCopy;
                        return toCopy;
                    }
                }

                // Idle link: nothing to hand over, and no busy-spin in the reader thread.
                Thread.Sleep(10);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
