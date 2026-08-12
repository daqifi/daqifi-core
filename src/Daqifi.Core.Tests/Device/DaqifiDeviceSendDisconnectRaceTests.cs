using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for <see cref="DaqifiDevice.Send{T}"/> racing a teardown (#497).
/// </summary>
/// <remarks>
/// <para>
/// The contract under test: a send that loses a race against <c>Disconnect</c>, <c>Dispose</c> or an
/// auto-reconnect fails as <see cref="DeviceNotConnectedException"/> with
/// <see cref="DeviceNotConnectedException.IsShuttingDown"/> set — never as a
/// <see cref="NullReferenceException"/> from a half-torn-down field, and never as a bare
/// <see cref="InvalidOperationException"/> whose message names an internal producer lifecycle
/// method. Both outcomes were reachable before: the send path null-checked the mutable
/// <c>_messageProducer</c> field and then dereferenced it (two reads, teardown could land between
/// them), and winning that check still left the producer free to stop before the message was
/// queued.
/// </para>
/// <para>
/// The race itself cannot be scheduled, so it is covered from both ends: the translation is pinned
/// deterministically through the internal seam (including against a real
/// <see cref="MessageProducer{T}"/>, so a change to what it throws is caught here), and the real
/// race is run as a stress loop that asserts the negative.
/// </para>
/// </remarks>
public class DaqifiDeviceSendDisconnectRaceTests
{
    /// <summary>How long the disconnect/reconnect stress loop is allowed to run.</summary>
    private static readonly TimeSpan StressBudget = TimeSpan.FromSeconds(4);

    /// <summary>Upper bound on stress cycles, so a fast machine stops early instead of spinning.</summary>
    private const int MaxStressCycles = 25;

    // ── Translation: the producer reports a teardown ────────────────────────────────────────

    [Fact]
    public void SendViaProducer_WhenProducerIsNotRunning_ThrowsDeviceNotConnected()
    {
        // The exact exception MessageProducer.Send raises once Stop/StopSafely has run.
        var stopped = new InvalidOperationException("Message producer is not running. Call Start() first.");
        var producer = new ThrowingProducer(stopped);

        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.True(ex.IsShuttingDown, "A producer stopped by teardown is a shutdown, not a plain 'never connected'.");
        Assert.Same(stopped, ex.InnerException);
    }

    [Fact]
    public void SendViaProducer_WhenProducerIsDisposed_ThrowsDeviceNotConnected()
    {
        // ObjectDisposedException derives from InvalidOperationException, so this also pins the
        // order of the two catch blocks: a broader-first ordering would report the disposed
        // producer as merely "no longer running".
        var disposed = new ObjectDisposedException("MessageProducer");
        var producer = new ThrowingProducer(disposed);

        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.True(ex.IsShuttingDown);
        Assert.Same(disposed, ex.InnerException);
        Assert.Contains("disposed", ex.Message);
    }

    [Fact]
    public void SendViaProducer_WhenProducerThrowsDeviceNotConnected_RethrowsItUnchanged()
    {
        // Already the failure this would translate to; re-wrapping would bury the producer's own
        // wording one level deeper for no gain.
        var original = new DeviceNotConnectedException("producer said so", isShuttingDown: true);
        var producer = new ThrowingProducer(original);

        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.Same(original, ex);
    }

    [Fact]
    public void SendViaProducer_DoesNotTranslateFailuresThatAreNotTeardowns()
    {
        // The translation is scoped to lifecycle failures. An I/O fault is a real fault and must
        // keep its type, or callers lose the ability to tell a dead link from a closed one.
        var faulted = new IOException("stream fault");
        var producer = new ThrowingProducer(faulted);

        var ex = Assert.Throws<IOException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.Same(faulted, ex);
    }

    [Fact]
    public void SendViaProducer_OnTheHappyPath_HandsTheMessageToTheProducer()
    {
        var producer = new ThrowingProducer(failure: null);

        // Held in a local: ScpiMessageProducer.GetDeviceInfo builds a fresh message per access.
        var message = ScpiMessageProducer.GetDeviceInfo;

        DaqifiDevice.SendViaProducer(producer, message);

        Assert.Single(producer.Sent);
        Assert.Same(message, producer.Sent[0]);
    }

    // ── Translation: pinned against the real producer ───────────────────────────────────────

    [Fact]
    public void SendViaProducer_AgainstARealStoppedProducer_ThrowsDeviceNotConnected()
    {
        using var sink = new NullSinkStream();
        using var producer = new MessageProducer<string>(sink);
        producer.Start();
        producer.StopSafely();

        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.True(ex.IsShuttingDown);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void SendViaProducer_AgainstARealDisposedProducer_ThrowsDeviceNotConnected()
    {
        using var sink = new NullSinkStream();
        var producer = new MessageProducer<string>(sink);
        producer.Start();
        producer.Dispose();

        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => DaqifiDevice.SendViaProducer(producer, ScpiMessageProducer.GetDeviceInfo));

        Assert.True(ex.IsShuttingDown);
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    // ── The real race ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Send_RacingRepeatedDisconnectAndReconnect_OnlySurfacesTypedFailures()
    {
        using var transport = new SinkTransport();
        using var device = new DaqifiDevice("Racing Device", transport);
        device.Connect();

        // Only the first untyped failure is kept. It is the one that fails the test, and holding on
        // to every teardown exception a four-second loop produces would have the test allocating
        // harder than the race it is trying to lose. Typed failures are counted rather than stored:
        // the count is diagnostic only, since whether a given run actually hits the race is not
        // something a test can schedule.
        Exception? untyped = null;
        var typedFailures = 0;
        var sends = 0;
        var stop = false;

        var sender = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    device.Send(ScpiMessageProducer.GetDeviceInfo);
                    Interlocked.Increment(ref sends);
                }
                catch (DeviceNotConnectedException)
                {
                    // The expected way to lose this race, and the whole point of the fix.
                    Interlocked.Increment(ref typedFailures);
                }
                catch (Exception ex)
                {
                    // First one wins and ends the run: it already fails the test, and letting the
                    // loop carry on only buries it under thousands more of the same.
                    Interlocked.CompareExchange(ref untyped, ex, null);
                    Volatile.Write(ref stop, true);
                }

                // Keeps the producer's unbounded queue from being the thing under test.
                Thread.Yield();
            }
        })
        { IsBackground = true };

        sender.Start();

        var cycles = 0;
        var clock = Stopwatch.StartNew();

        // The sender raises `stop` the moment something untyped escapes, so a failing run reports in
        // milliseconds instead of burning the whole budget cycling a device nobody is sending to.
        while (cycles < MaxStressCycles && clock.Elapsed < StressBudget && !Volatile.Read(ref stop))
        {
            device.Disconnect();
            device.Connect();
            cycles++;
        }

        Volatile.Write(ref stop, true);
        Assert.True(sender.Join(TimeSpan.FromSeconds(10)), "The sender thread did not stop.");

        // The loop has to have actually run, or the assertions below are vacuous.
        Assert.True(cycles > 0, "No disconnect/reconnect cycles ran.");
        Assert.True(Volatile.Read(ref sends) > 0, "No sends were attempted.");

        // Asserted as a negative on purpose: whether a given run wins or loses the race is not
        // something a test can schedule, so this pins "no untyped failure can escape" rather than
        // "the race happened". Before the fix, a long enough run produced both a
        // NullReferenceException, from dereferencing the field teardown had just nulled, and a bare
        // InvalidOperationException from the stopped producer; either one lands here. Read plainly —
        // Thread.Join already published everything the sender wrote.
        var escaped = untyped;
        Assert.True(
            escaped == null,
            $"Send surfaced an untyped failure while racing teardown (after {typedFailures} typed "
            + $"failure(s)): {escaped?.GetType().Name}: {escaped?.Message}");

        device.Disconnect();
    }

    [Fact]
    public void Send_WhenConnected_StillReachesTheWire()
    {
        // The snapshot must not have cost the common path: a connected device's string message
        // still goes through the queued producer rather than the direct-write fallback.
        using var transport = new SinkTransport();
        using var device = new DaqifiDevice("Happy Device", transport);
        device.Connect();

        device.Send(ScpiMessageProducer.GetDeviceInfo);

        Assert.True(
            SpinWait.SpinUntil(() => transport.BytesWritten > 0, TimeSpan.FromSeconds(5)),
            "A message sent on a connected device never reached the stream.");

        device.Disconnect();
    }

    // ── Test doubles ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A producer that either records what it was handed or throws a caller-supplied failure, so
    /// the translation can be driven through every branch without scheduling a race.
    /// </summary>
    private sealed class ThrowingProducer : IMessageProducer<string>
    {
        private readonly Exception? _failure;

        public ThrowingProducer(Exception? failure) => _failure = failure;

        public List<IOutboundMessage<string>> Sent { get; } = new();

        /// <summary>
        /// Never raised — this double fails synchronously out of <see cref="Send"/>, which is the
        /// whole point of it. Declared with explicit accessors so it needs no backing field.
        /// </summary>
        public event EventHandler<MessageSendFailedEventArgs<string>>? SendFailed
        {
            add { }
            remove { }
        }

        public int QueuedMessageCount => 0;

        public bool IsRunning => _failure == null;

        public void Send(IOutboundMessage<string> message)
        {
            if (_failure != null)
            {
                throw _failure;
            }

            Sent.Add(message);
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public bool StopSafely(int timeoutMs = 1000) => true;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A write-only stream that counts bytes and keeps none of them, so a stress loop's traffic
    /// cannot itself become the memory or contention under test.
    /// </summary>
    private sealed class NullSinkStream : Stream
    {
        private long _bytesWritten;

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

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
            // Nothing ever arrives; back off so the consumer's reader loop does not spin a core.
            Thread.Sleep(5);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Interlocked.Add(ref _bytesWritten, count);
    }

    /// <summary>
    /// Transport over a <see cref="NullSinkStream"/> that can be connected and disconnected
    /// repeatedly, which is what makes the device rebuild and re-null its message pumps the way an
    /// auto-reconnect does.
    /// </summary>
    private sealed class SinkTransport : IStreamTransport
    {
        private readonly NullSinkStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public long BytesWritten => _stream.BytesWritten;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(SinkTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Sink: Connected" : "Sink: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SinkTransport));
            }

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
            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
