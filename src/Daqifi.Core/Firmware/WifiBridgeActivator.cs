using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Transient SCPI-over-serial helper that kicks the device's WiFi module into
/// bridge mode during a firmware update.
/// </summary>
/// <remarks>
/// Used between the initial LAN-FW-update-mode handshake (sent over the regular
/// device transport) and the WINC flash tool's programming phase.
/// The flash tool needs exclusive access to the serial port, so this
/// helper opens a short-lived transport, sends the
/// <c>SYSTem:COMMUnicate:LAN:FWUpdate</c> / <c>SYSTem:COMMunicate:LAN:APPLY</c>
/// pair with the timing the firmware's WiFi-deinit/reinit state machine
/// expects, then closes the port so it can be handed back.
/// </remarks>
/// <remarks>
/// <see cref="Activate(string, CancellationToken)"/> and
/// <see cref="Deactivate(string, CancellationToken)"/> call
/// <see cref="SerialStreamTransport.Connect"/> synchronously, which ultimately
/// calls <see cref="System.IO.Ports.SerialPort.Open"/>. That call can hang
/// uncancellably in native code (see the discovery-side isolation in
/// <c>SerialDeviceFinder</c>, #294/#295) — a stuck open blocks the calling
/// thread indefinitely and no <see cref="CancellationToken"/> can interrupt it
/// once the call has begun. Do not call the synchronous overloads from a UI
/// thread; prefer <see cref="ActivateAsync(string, CancellationToken)"/> /
/// <see cref="DeactivateAsync(string, CancellationToken)"/>, which isolate the
/// call onto a worker task and race it against a hard timeout.
/// </remarks>
public static class WifiBridgeActivator
{
    // USB CDC virtual ports ignore the baud rate, but firmware checks DTR
    // (isCdcHostConnected) before accepting commands. Match SerialStreamTransport
    // defaults so port settings stay in one place.
    private static readonly TimeSpan DtrSettleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan InterCommandDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ApplySettleDelay = TimeSpan.FromMilliseconds(300);

    // Hard ceiling for the async overloads' worker task. The healthy path costs
    // ~600ms of scripted delays (DtrSettleDelay + InterCommandDelay +
    // ApplySettleDelay) plus Open() and two near-instant writes; 5s gives
    // generous headroom for a slow open while still abandoning a genuinely
    // wedged port (SerialPort.Open() stuck in native GetCommState, same failure
    // mode as #294) in bounded time instead of hanging forever.
    private const int DefaultHardTimeoutMs = 5000;

    // Internal so tests can shrink the hard timeout instead of waiting 5s per case.
    internal static int HardTimeoutMs { get; set; } = DefaultHardTimeoutMs;

    /// <summary>
    /// Briefly opens <paramref name="portName"/>, sends the SCPI sequence that
    /// puts the device's WiFi module into bridge mode for firmware update, then
    /// closes the port.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed between port-open, each command write, and each inter-command delay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static void Activate(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        using var transport = new SerialStreamTransport(portName);
        transport.Connect();

        // Allow firmware to recognise DTR before sending commands.
        WaitOrCancel(DtrSettleDelay, cancellationToken);

        // Re-assert the FW-update-requested flag (idempotent with the earlier handshake).
        WriteCommand(transport, ScpiMessageProducer.SetLanFirmwareUpdateMode, cancellationToken);
        WaitOrCancel(InterCommandDelay, cancellationToken);

        // Trigger the WiFi manager REINIT to bridge-mode state machine.
        WriteCommand(transport, ScpiMessageProducer.ApplyNetworkLan, cancellationToken);

        // Give firmware time to enqueue APPLY before the port closes.
        WaitOrCancel(ApplySettleDelay, cancellationToken);
    }

    /// <summary>
    /// Briefly opens <paramref name="portName"/>, sends the SCPI sequence that
    /// takes the device's WiFi module back out of bridge mode after a firmware
    /// update, then closes the port.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed between port-open, each command write, and each inter-command delay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static void Deactivate(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        using var transport = new SerialStreamTransport(portName);
        transport.Connect();

        // Allow firmware to recognise DTR before sending commands.
        WaitOrCancel(DtrSettleDelay, cancellationToken);

        // Turn off transparent mode so the port goes back to the SCPI console.
        WriteCommand(transport, ScpiMessageProducer.SetUsbTransparencyMode(0), cancellationToken);
        WaitOrCancel(InterCommandDelay, cancellationToken);

        // Trigger the WiFi manager REINIT out of bridge-mode state machine.
        WriteCommand(transport, ScpiMessageProducer.ApplyNetworkLan, cancellationToken);

        // Give firmware time to enqueue APPLY before the port closes.
        WaitOrCancel(ApplySettleDelay, cancellationToken);
    }

    /// <summary>
    /// Async, isolated equivalent of <see cref="Activate(string, CancellationToken)"/>.
    /// Runs the synchronous sequence on a worker task and races it against a
    /// hard timeout, so a wedged <see cref="System.IO.Ports.SerialPort.Open"/>
    /// cannot block the caller indefinitely.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed up front and while waiting for the worker task.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="TimeoutException">The worker task did not complete within the hard timeout.</exception>
    public static Task ActivateAsync(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        return RunWithHardTimeoutAsync(
            token => Activate(portName, token),
            nameof(ActivateAsync),
            cancellationToken);
    }

    /// <summary>
    /// Async, isolated equivalent of <see cref="Deactivate(string, CancellationToken)"/>.
    /// Runs the synchronous sequence on a worker task and races it against a
    /// hard timeout, so a wedged <see cref="System.IO.Ports.SerialPort.Open"/>
    /// cannot block the caller indefinitely.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed up front and while waiting for the worker task.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="TimeoutException">The worker task did not complete within the hard timeout.</exception>
    public static Task DeactivateAsync(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        return RunWithHardTimeoutAsync(
            token => Deactivate(portName, token),
            nameof(DeactivateAsync),
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a worker task and races it against
    /// <see cref="HardTimeoutMs"/>, mirroring the isolation pattern
    /// <c>SerialDeviceFinder</c> uses around <see cref="System.IO.Ports.SerialPort.Open"/>
    /// (#294/#295). Internal (not private) so tests can exercise the timeout /
    /// abandonment path directly with a synthetic hang, without needing a real
    /// wedged serial port.
    /// </summary>
    /// <param name="operation">
    /// The synchronous operation to isolate. Receives a token that is already
    /// cancelled once the hard timeout elapses — <see cref="Activate(string, CancellationToken)"/>
    /// / <see cref="Deactivate(string, CancellationToken)"/> observe it between
    /// port-open and each subsequent delay/write, so a late-returning
    /// <see cref="System.IO.Ports.SerialPort.Open"/> still stops the sequence
    /// before it sends any SCPI command (Qodo review #326 #2: a late open must
    /// not let an abandoned worker mutate device state after the caller already
    /// received a <see cref="TimeoutException"/>).
    /// </param>
    /// <param name="operationName">Used only in the <see cref="TimeoutException"/> message.</param>
    /// <param name="cancellationToken">Cancellation token observed while waiting for the worker task.</param>
    internal static async Task RunWithHardTimeoutAsync(Action<CancellationToken> operation, string operationName, CancellationToken cancellationToken)
    {
        var hardTimeoutMs = HardTimeoutMs;

        // hardTimeoutCts fires on its own timer independent of the Task.Delay race
        // below, so it reaches the worker even if Open() only returns long after
        // the race has already been decided. linkedCts is what the worker actually
        // observes: caller cancellation OR the hard timeout, whichever comes first.
        var hardTimeoutCts = new CancellationTokenSource(hardTimeoutMs);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardTimeoutCts.Token);

        // LongRunning (a dedicated thread, not a pooled one): the operation's
        // SerialPort.Open() and blocking stream writes would otherwise run on the
        // CALLING thread up to first await — on a UI thread that means a stuck open
        // freezes the window. A plain Task.Run would occupy a thread-pool worker for
        // the operation's full (potentially unbounded) blocking duration; under
        // thread-pool pressure that can starve the pool queue the hardTimeoutCts
        // timer callback also runs on, delaying the very cancellation this method
        // exists to deliver. Pass CancellationToken.None to StartNew itself: the
        // inner token still cancels the operation's own delays; we never want
        // "cancelled before start" to look like an operation fault.
        var workerTask = Task.Factory.StartNew(
            () => operation(linkedCts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            var winner = await Task.WhenAny(
                workerTask,
                Task.Delay(hardTimeoutMs, cancellationToken)).ConfigureAwait(false);

            // Only abandon when the worker is genuinely still running. WhenAny can
            // return the delay/cancellation task even though workerTask completed
            // right at that same boundary — awaiting it below instead of throwing
            // honors that result instead of discarding it (Qodo review #326 #1,
            // same race SerialDeviceFinder guards against per #295).
            if (winner != workerTask && !workerTask.IsCompleted)
            {
                // Open() is uncancellable, so on timeout/cancellation the worker task
                // is ABANDONED rather than awaited — it may still be blocked in native
                // I/O. Observe its eventual fault so it can't surface as an
                // UnobservedTaskException once the stuck I/O finally completes, and
                // dispose the token sources only once the worker is done with them
                // (disposing early would make its still-pending WaitOrCancel/
                // WriteCommand calls throw ObjectDisposedException instead of
                // observing cancellation).
                _ = workerTask.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        linkedCts.Dispose();
                        hardTimeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                // Prefer surfacing caller cancellation over a generic timeout when
                // both raced at once.
                cancellationToken.ThrowIfCancellationRequested();

                throw new TimeoutException(
                    $"{operationName} did not complete within {hardTimeoutMs}ms; the serial port open may be permanently blocked.");
            }

            // Propagate success or the operation's own exception unchanged.
            await workerTask.ConfigureAwait(false);
        }
        finally
        {
            // The abandon path above transfers disposal ownership to its own
            // continuation instead — only dispose here when the worker already
            // finished (the common, non-hung case).
            if (workerTask.IsCompleted)
            {
                linkedCts.Dispose();
                hardTimeoutCts.Dispose();
            }
        }
    }

    private static void WriteCommand(SerialStreamTransport transport, IOutboundMessage<string> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = message.GetBytes();
        var stream = transport.Stream;
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static void WaitOrCancel(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        else
        {
            Thread.Sleep(delay);
        }
    }
}
