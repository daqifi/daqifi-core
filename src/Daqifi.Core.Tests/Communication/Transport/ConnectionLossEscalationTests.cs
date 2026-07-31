using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Issues #377 and #394: a connection that dies mid-stream must reach the device as
/// <see cref="ConnectionStatus.Lost"/> rather than leaving it reporting <c>Connected</c> forever,
/// and an intentional disconnect must never be mistaken for one.
/// </summary>
/// <remarks>
/// The TCP half — a peer that closes the socket, driven over a real loopback connection — lives in
/// <see cref="TcpStreamTransportDropDetectionTests"/>. These cover the serial-shaped path (a read
/// that starts throwing) end to end through a device, and the one remaining place the reader loop
/// could still spin silently forever: a stream that has stopped being readable at all.
/// </remarks>
public class ConnectionLossEscalationTests
{
    [Fact]
    public void AMidStreamReadFailure_TransitionsTheDeviceToLost()
    {
        // The serial-unplug shape: reads start throwing and never recover.
        using var transport = new WatchedFailingTransport();
        using var device = new DaqifiDevice("Unplugged Device", transport);

        var lost = new ManualResetEventSlim(false);
        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Lost)
            {
                lost.Set();
            }
        };

        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        transport.FailingStream.FailReads = true;

        Assert.True(lost.Wait(TimeSpan.FromSeconds(10)), "the device never reported ConnectionStatus.Lost");
        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void AMidStreamReadFailure_AlsoReachesTheDeviceErrorSurface()
    {
        // #377 escalates; #378 explains. Both are fed by the same reader-loop failure.
        using var transport = new WatchedFailingTransport();
        using var device = new DaqifiDevice("Unplugged Device", transport);

        var raised = new ManualResetEventSlim(false);
        DeviceErrorEventArgs? captured = null;
        device.ErrorOccurred += (_, e) =>
        {
            captured = e;
            raised.Set();
        };

        device.Connect();
        transport.FailingStream.FailReads = true;

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(DeviceErrorSource.MessageConsumer, captured!.Source);
    }

    [Fact]
    public void ASingleFailedRead_DoesNotReportLost()
    {
        using var transport = new WatchedFailingTransport();
        using var device = new DaqifiDevice("Blipping Device", transport);

        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status);
            }
        };

        device.Connect();

        // One failure, then the stream goes back to idling — a glitch, not a disconnect. Escalation
        // needs a run of five, so nothing may be reported. (That a *successful* read clears an
        // accumulated run is covered by StreamMessageConsumerHealthReportingTests.)
        transport.FailingStream.FailOnce();

        Thread.Sleep(600);

        lock (statuses)
        {
            Assert.DoesNotContain(ConnectionStatus.Lost, statuses);
        }

        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void AnIntentionalDisconnect_NeverReportsLost_EvenThoughTeardownFailsTheReads()
    {
        using var transport = new WatchedFailingTransport();
        using var device = new DaqifiDevice("Departing Device", transport);

        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status);
            }
        };

        // Closing the handle is what makes the in-flight reads fail, exactly as a real transport
        // teardown does. None of that may be reported as a loss.
        device.Disconnect();

        Thread.Sleep(600);

        lock (statuses)
        {
            Assert.DoesNotContain(ConnectionStatus.Lost, statuses);
            Assert.Contains(ConnectionStatus.Disconnected, statuses);
        }

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void AStreamThatIsNoLongerReadable_IsReportedInsteadOfSpunOnSilently()
    {
        // The last silent-spin path in the reader loop (issue #377): a stream that reports itself
        // unreadable never becomes readable again, so backing off and looping forever produced no
        // data, no error and no status change simultaneously.
        using var stream = new UnreadableStream();
        var sink = new RecordingHealthSink();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();

        Assert.True(WaitUntil(
            () => sink.FaultCount >= TransportConnectionWatchdog.ConsecutiveFaultThreshold,
            TimeSpan.FromSeconds(10)),
            $"expected the unreadable stream to be escalated, saw {sink.FaultCount} fault(s)");
        Assert.True(Volatile.Read(ref errors) >= 1);

        consumer.StopSafely(timeoutMs: 2000);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    /// <summary>
    /// Records what the reader loop reports, standing in for a real transport.
    /// </summary>
    private sealed class RecordingHealthSink : ITransportHealthSink
    {
        private int _faultCount;

        public int FaultCount => Volatile.Read(ref _faultCount);

        public void ReportIoFault(Exception error) => Interlocked.Increment(ref _faultCount);

        public void ReportIoSuccess()
        {
        }
    }

    /// <summary>
    /// A transport whose stream can be made to fail, wired to the same
    /// <see cref="TransportConnectionWatchdog"/> the real serial and TCP transports use — so this
    /// exercises the production escalation rules, not a test-only approximation of them.
    /// </summary>
    private sealed class WatchedFailingTransport : IStreamTransport, ITransportHealthSink
    {
        private readonly TransportConnectionWatchdog _watchdog;
        private bool _isConnected;
        private bool _disposed;

        public WatchedFailingTransport()
        {
            _watchdog = new TransportConnectionWatchdog("Test transport", HandleConnectionLost);
        }

        public FailableStream FailingStream { get; } = new();

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(WatchedFailingTransport))
            : FailingStream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => IsConnected ? "Test: Connected" : "Test: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            _watchdog.Arm();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            // Disarm before tearing anything down, exactly as the real transports do: closing the
            // handle makes in-flight reads fail, and none of that is a lost connection.
            _watchdog.Disarm();

            _isConnected = false;
            FailingStream.FailReads = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        public void ReportIoFault(Exception error) => _watchdog.RecordFault(error);

        public void ReportIoSuccess() => _watchdog.RecordSuccess();

        private void HandleConnectionLost(Exception error)
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo, error));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isConnected = false;
            _watchdog.Dispose();
            FailingStream.Dispose();
        }
    }

    /// <summary>
    /// A stream that idles quietly until told to fail its reads.
    /// </summary>
    internal sealed class FailableStream : Stream
    {
        private int _failuresRemaining = -1;

        public volatile bool FailReads;

        /// <summary>
        /// Fails exactly one read, then goes back to idling — a glitch rather than a disconnect.
        /// </summary>
        public void FailOnce() => Interlocked.Exchange(ref _failuresRemaining, 1);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads)
            {
                Thread.Sleep(5);
                throw new IOException("the device is gone");
            }

            if (Volatile.Read(ref _failuresRemaining) > 0)
            {
                Interlocked.Decrement(ref _failuresRemaining);
                throw new IOException("a transient read glitch");
            }

            // Idle: the device has nothing to say right now. This is not a socket, so a zero-byte
            // read is "nothing yet" and is never reported as a fault.
            Thread.Sleep(20);
            return 0;
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

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    /// <summary>
    /// A stream that is open but permanently unreadable — the shape a closed or disposed underlying
    /// handle presents to the reader loop.
    /// </summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
