using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Coverage for #493 — the raw-capture path (what an SD download runs on) must take the device's
/// operation lock, not just the SD download gate.
/// </summary>
/// <remarks>
/// Before the fix a capture suspended the protobuf consumer and took the transport stream while
/// excluding nothing: a text query from another thread acquired the exchange lock uncontended and
/// started a second reader on that same stream, and a plain <see cref="DaqifiDevice.Send{T}"/> was
/// not deferred at all, so its bytes went out mid-transfer and the reply landed inside the captured
/// file. These tests pin both halves — mutual exclusion against text exchanges, and deferral of
/// other flows' sends — plus the lock hygiene that comes with holding a lock: nested re-entry,
/// release on every exit path, and validation performed after the wait rather than before it.
/// </remarks>
public class DaqifiDeviceRawCaptureLockTests
{
    private static readonly TimeSpan DeadlockBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Long enough that a send which was NOT deferred would have reached the wire — the queued
    /// producer path writes on its own thread, so "nothing yet" has to be given time to be wrong.
    /// </summary>
    private static readonly TimeSpan WireSettleWait = TimeSpan.FromMilliseconds(250);

    // ── Mutual exclusion against text exchanges ─────────────────────────────────────────────

    [Fact]
    public async Task TextExchange_StartedDuringARawCapture_WaitsForTheCaptureToFinish()
    {
        // The corruption this closes: the text exchange's own consumer swap would put a SECOND
        // reader on the stream the capture is draining.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Capturing Device", transport);
        device.Connect();

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureLeft = 0;
        var exchangeSawCaptureStillRunning = false;

        var capture = Task.Run(() => device.RunRawCaptureAsync(async (_, ct) =>
        {
            captureEntered.SetResult();
            await releaseCapture.Task.WaitAsync(ct);
            Volatile.Write(ref captureLeft, 1);
        }));

        await captureEntered.Task.WaitAsync(DeadlockBudget);

        var exchange = Task.Run(() => device.RunTextExchangeAsync(() =>
        {
            // Reached only once the capture is done; if the lock were not taken this would run
            // while the capture still owned the stream.
            if (Volatile.Read(ref captureLeft) == 0)
            {
                exchangeSawCaptureStillRunning = true;
            }

            device.Send(ScpiMessageProducer.GetDeviceInfo);
        }));

        // The exchange must be parked on the lock, not running: give it far longer than it needs
        // to get to its setup action.
        await Task.Delay(WireSettleWait);
        Assert.False(exchange.IsCompleted, "The text exchange ran while a raw capture held the device.");

        releaseCapture.SetResult();
        await capture.WaitAsync(DeadlockBudget);
        await exchange.WaitAsync(DeadlockBudget);

        Assert.False(
            exchangeSawCaptureStillRunning,
            "The text exchange's setup action ran before the raw capture had finished.");

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_StartedDuringAnExclusiveOperation_WaitsForIt()
    {
        // The mirror direction: whoever holds the device first keeps it.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Waiting Device", transport);
        device.Connect();

        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationLeft = 0;
        var captureSawOperationStillRunning = false;

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            operationEntered.SetResult();
            await releaseOperation.Task;
            Volatile.Write(ref operationLeft, 1);
        }));

        await operationEntered.Task.WaitAsync(DeadlockBudget);

        var capture = Task.Run(() => device.RunRawCaptureAsync((_, _) =>
        {
            if (Volatile.Read(ref operationLeft) == 0)
            {
                captureSawOperationStillRunning = true;
            }

            return Task.CompletedTask;
        }));

        await Task.Delay(WireSettleWait);
        Assert.False(capture.IsCompleted, "A raw capture started while an exclusive operation owned the device.");

        releaseOperation.SetResult();
        await operation.WaitAsync(DeadlockBudget);
        await capture.WaitAsync(DeadlockBudget);

        Assert.False(
            captureSawOperationStillRunning,
            "The raw capture ran before the exclusive operation had finished.");

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_FromInsideAnExclusiveOperation_RunsNestedWithoutDeadlocking()
    {
        // The SD operations reach the capture from flows that may already own the lock (#407), so
        // re-entry has to run nested rather than wait on a semaphore this flow is holding.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Nesting Device", transport);
        device.Connect();

        var reached = false;

        await device.RunExclusiveAsync(ct => device.RunRawCaptureAsync((_, _) =>
        {
            reached = true;
            return Task.CompletedTask;
        }, ct)).WaitAsync(DeadlockBudget);

        Assert.True(reached);

        // The nested capture must not have released the outer flow's lock on its way out.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Send deferral ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_DuringARawCapture_IsDeferredAndReplayedAfterwards()
    {
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Deferring Device", transport);
        device.Connect();

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = Task.Run(() => device.RunRawCaptureAsync(async (_, ct) =>
        {
            captureEntered.SetResult();
            await releaseCapture.Task.WaitAsync(ct);
        }));

        await captureEntered.Task.WaitAsync(DeadlockBudget);

        await Task.Run(() => device.Send(ScpiMessageProducer.SetDioPortState(4, 1)));

        await Task.Delay(WireSettleWait);
        Assert.DoesNotContain(transport.Writes, w => w.Text.Contains("DIO:PORt:STATe", StringComparison.Ordinal));

        releaseCapture.SetResult();
        await capture.WaitAsync(DeadlockBudget);

        await WaitForWriteAsync(transport, "DIO:PORt:STATe");

        device.Disconnect();
    }

    [Fact]
    public async Task Send_DuringARawCapture_StillReturnsImmediately()
    {
        // Deferred, not blocked: Send is fire-and-forget and must stay that way even when the
        // device is owned by a 30-minute download.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Non-blocking Device", transport);
        device.Connect();

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = Task.Run(() => device.RunRawCaptureAsync(async (_, ct) =>
        {
            captureEntered.SetResult();
            await releaseCapture.Task.WaitAsync(ct);
        }));

        await captureEntered.Task.WaitAsync(DeadlockBudget);

        var send = Task.Run(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
        var returned = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(2))) == send;

        releaseCapture.SetResult();
        await capture.WaitAsync(DeadlockBudget);
        await send.WaitAsync(DeadlockBudget);

        Assert.True(returned, "Send blocked while a raw capture owned the device.");

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_UnderASendHammer_ReadsThePayloadWithNothingInterleaved()
    {
        // The end-to-end shape of the bug: a caller polling status or firing commands while a
        // download runs. Every one of those sends used to hit the wire mid-transfer, so the
        // device's replies landed inside the file's bytes.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Hammered Device", transport);
        device.Connect();

        var payload = new byte[4096];
        new Random(493).NextBytes(payload);
        transport.CaptureStream.Payload = payload;

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopHammer = 0;
        var received = new MemoryStream();

        var capture = Task.Run(() => device.RunRawCaptureAsync(async (stream, ct) =>
        {
            transport.CaptureStream.CaptureWindowOpen = true;
            try
            {
                captureEntered.SetResult();

                var buffer = new byte[256];
                while (received.Length < payload.Length)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        received.Write(buffer, 0, read);
                    }

                    // Widen the window so the hammer has every chance to interleave.
                    await Task.Delay(1, ct);
                }
            }
            finally
            {
                transport.CaptureStream.CaptureWindowOpen = false;
            }
        }));

        await captureEntered.Task.WaitAsync(DeadlockBudget);

        var hammer = Task.Run(() =>
        {
            var sent = 0;
            while (Volatile.Read(ref stopHammer) == 0)
            {
                device.Send(new TaggedBinaryMessage($"HAMMER-{sent++}"));
                Thread.Sleep(1);
            }

            return sent;
        });

        await capture.WaitAsync(DeadlockBudget);
        Volatile.Write(ref stopHammer, 1);
        var hammered = await hammer.WaitAsync(DeadlockBudget);

        Assert.True(hammered > 0, "The hammer never sent anything, so this proves nothing.");
        Assert.Equal(payload, received.ToArray());

        var duringCapture = transport.Writes.Where(w => w.DuringCapture).ToList();
        Assert.True(
            duringCapture.Count == 0,
            $"{duringCapture.Count} write(s) reached the wire during the capture: "
            + string.Join(" | ", duringCapture.Take(5).Select(w => w.Text)));

        // And they were parked, not dropped.
        await WaitForWriteAsync(transport, "HAMMER-0");

        device.Disconnect();
    }

    // ── Nested consumer swaps ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawCapture_NestedInsideAnotherRawCapture_IsRejected()
    {
        // The lock deliberately lets a flow that already owns it run nested — which is exactly what
        // makes a nested swap silent. The inner capture's finally would restart the protobuf
        // consumer while the outer one still had the stream, putting a second reader on it.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Nested-swap Device", transport);
        device.Connect();

        Exception? inner = null;

        await device.RunRawCaptureAsync(async (_, ct) =>
        {
            inner = await Record.ExceptionAsync(
                () => device.RunRawCaptureAsync((_, _) => Task.CompletedTask, ct));
        }).WaitAsync(DeadlockBudget);

        var rejected = Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("not re-entrant", rejected.Message, StringComparison.Ordinal);

        // The guard is per-flow state, so it must be cleared: a later capture still works.
        await device.RunRawCaptureAsync((_, _) => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task TextExchange_FromInsideARawCapture_IsRejected()
    {
        // Same corruption from the other direction, and the one a caller is likelier to write:
        // "while I have the stream, let me just ask the device something."
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Nested-exchange Device", transport);
        device.Connect();

        Exception? inner = null;

        await device.RunRawCaptureAsync(async (_, ct) =>
        {
            inner = await Record.ExceptionAsync(
                () => device.RunTextExchangeAsync(() => device.Send(ScpiMessageProducer.GetDeviceInfo), ct));
        }).WaitAsync(DeadlockBudget);

        var rejected = Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("not re-entrant", rejected.Message, StringComparison.Ordinal);

        // And the outer capture left the device usable.
        await device.RunTextExchangeAsync(() => device.Send(ScpiMessageProducer.GetDeviceInfo))
            .WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Lock hygiene ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawCapture_ReleasesTheLockWhenTheActionThrows()
    {
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Throwing Device", transport);
        device.Connect();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.RunRawCaptureAsync((_, _) => throw new InvalidOperationException("boom")));

        // A leaked lock would hang here instead of completing.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_ReleasesTheLockAfterAValidationFailure()
    {
        // Not connected: the failure now happens with the lock held, so the release has to run on
        // that path too. Two calls, then an unrelated operation — any of them would hang on a leak.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Unconnected Device", transport);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.RunRawCaptureAsync((_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.RunRawCaptureAsync((_, _) => Task.CompletedTask));

        device.Connect();
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_WhenTheDeviceDisconnectsWhileItWaits_FailsInsteadOfTakingADeadStream()
    {
        // The reason validation moved inside the lock: everything checked before the wait was
        // checked against a session that can be gone by the time the lock arrives.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Torn-down Device", transport);
        device.Connect();

        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            operationEntered.SetResult();
            await releaseOperation.Task;
        }));

        await operationEntered.Task.WaitAsync(DeadlockBudget);

        // Started from the test's own flow, deliberately: a Task.Run from INSIDE the exclusive
        // block would inherit that flow's ownership of the lock and run nested instead of queueing.
        var captureStarted = false;
        var capture = Task.Run(() => device.RunRawCaptureAsync((_, _) =>
        {
            captureStarted = true;
            return Task.CompletedTask;
        }));

        // Let it queue on the lock, then pull the session out from under it. The cancelled token
        // shortens teardown's courtesy wait for a lock the operation is still holding.
        await Task.Delay(WireSettleWait);
        using var shortWait = new CancellationTokenSource();
        await shortWait.CancelAsync();
        await device.DisconnectAsync(shortWait.Token).WaitAsync(DeadlockBudget);

        releaseOperation.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => capture.WaitAsync(DeadlockBudget));
        Assert.False(captureStarted, "The raw capture took a stream from a session that had already been torn down.");
    }

    [Fact]
    public async Task RawCapture_WhenCancelledWhileWaitingForTheLock_DoesNotRun()
    {
        // Captures can be long, so a caller that cannot wait needs the token to work while queued.
        using var transport = new CaptureTransport();
        using var device = new RawCaptureDevice("Cancelling Device", transport);
        device.Connect();

        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            operationEntered.SetResult();
            await releaseOperation.Task;
        }));

        await operationEntered.Task.WaitAsync(DeadlockBudget);

        using var cts = new CancellationTokenSource();
        var started = false;
        var capture = Task.Run(() => device.RunRawCaptureAsync((_, _) =>
        {
            started = true;
            return Task.CompletedTask;
        }, cts.Token));

        await Task.Delay(WireSettleWait);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(DeadlockBudget));
        Assert.False(started);

        releaseOperation.SetResult();
        await operation.WaitAsync(DeadlockBudget);

        device.Disconnect();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static async Task WaitForWriteAsync(CaptureTransport transport, string fragment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.Writes.Any(w => w.Text.Contains(fragment, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"'{fragment}' never reached the wire.");
    }

    /// <summary>Exposes the protected raw-capture and text-exchange entry points.</summary>
    private sealed class RawCaptureDevice : DaqifiDevice
    {
        public RawCaptureDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task RunRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) =>
            ExecuteRawCaptureAsync(rawAction, cancellationToken);

        public Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Action setupAction,
            CancellationToken cancellationToken = default) =>
            ExecuteTextCommandAsync(
                setupAction,
                responseTimeoutMs: 300,
                completionTimeoutMs: 100,
                cancellationToken: cancellationToken);
    }

    /// <summary>A message that writes straight to the stream, so a send is observable the instant it happens.</summary>
    private sealed class TaggedBinaryMessage : IOutboundMessage<byte[]>
    {
        public TaggedBinaryMessage(string tag) => Data = Encoding.UTF8.GetBytes(tag);

        public byte[] Data { get; set; }

        public byte[] GetBytes() => Data;
    }

    private sealed record RecordedWrite(string Text, bool DuringCapture);

    private sealed class CaptureTransport : IStreamTransport
    {
        private bool _isConnected;
        private bool _disposed;

        public CaptureStream CaptureStream { get; } = new();

        public IReadOnlyList<RecordedWrite> Writes => CaptureStream.Writes;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(CaptureTransport))
            : CaptureStream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Capture: Connected" : "Capture: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureTransport));
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
    }

    /// <summary>
    /// Records every write together with whether the raw capture had the stream at that moment,
    /// and serves a scripted payload to the capture itself.
    /// </summary>
    private sealed class CaptureStream : Stream
    {
        private readonly List<RecordedWrite> _writes = new();
        private readonly object _gate = new();
        private int _payloadOffset;
        private volatile bool _captureWindowOpen;

        /// <summary>Set by the raw action around the window in which it owns the stream.</summary>
        public bool CaptureWindowOpen
        {
            get => _captureWindowOpen;
            set => _captureWindowOpen = value;
        }

        /// <summary>Bytes served to the capture, a chunk at a time. Null means "nothing to read".</summary>
        public byte[]? Payload { get; set; }

        public IReadOnlyList<RecordedWrite> Writes
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
            var payload = Payload;

            // Only the capture is served: outside its window this is the device's own consumer
            // polling, and handing it the file would make the test's premise false.
            if (payload == null || !_captureWindowOpen || _payloadOffset >= payload.Length)
            {
                // Nothing to read; back off so the consumer's reader loop doesn't spin.
                Thread.Sleep(5);
                return 0;
            }

            var take = Math.Min(Math.Min(count, 64), payload.Length - _payloadOffset);
            Array.Copy(payload, _payloadOffset, buffer, offset, take);
            _payloadOffset += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var duringCapture = _captureWindowOpen;
            lock (_gate)
            {
                _writes.Add(new RecordedWrite(Encoding.UTF8.GetString(buffer, offset, count), duringCapture));
            }
        }
    }
}
