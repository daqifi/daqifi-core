using System.IO.Ports;
using System.Runtime.ExceptionServices;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Serial port implementation of IStreamTransport that provides stream-based communication
/// over serial connections. Handles connection lifecycle and provides the underlying
/// SerialPort BaseStream for message producers and consumers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drop detection.</b> <see cref="SerialPort.IsOpen"/> reflects the OS handle, which stays open
/// after a USB device is physically unplugged, so it is not on its own a liveness signal. Two
/// detectors sit on top of it (issue #382), and whichever notices first closes the port and raises
/// <see cref="StatusChanged"/> with <c>IsConnected == false</c>, which a
/// <see cref="Device.DaqifiDevice"/> surfaces as <see cref="Device.DeviceState.Lost"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Port-presence polling</b> at <see cref="DefaultLivenessCheckInterval"/> (1 s), requiring two
/// consecutive misses. This bounds detection of an unplug at <b>roughly three to four seconds</b>
/// even on a completely idle connection (the extra second is the shared enumeration snapshot's
/// lifetime — see <see cref="SerialPortNameSnapshot"/>). Presence is the device node still existing
/// on Unix, where a port name is a path, falling back to <see cref="SerialPort.GetPortNames"/>
/// listing the port — no WMI, so it behaves identically on Windows, macOS, and Linux. It is armed
/// only if the port is visible to that probe at connect time, so a platform or port-name spelling
/// the probe cannot see disables the check rather than reporting a false drop.
/// </description></item>
/// <item><description>
/// <b>I/O fault escalation</b> via <see cref="ITransportHealthSink"/>: the reader/writer loops
/// report failures, and five consecutive failures with no successful transfer in between are
/// treated as a drop. Under traffic this reports within a few hundred milliseconds; a single
/// failed read that then recovers never disconnects, and a read or write that merely hits its
/// timeout is not a failure at all.
/// </description></item>
/// </list>
/// <para>
/// An intentional <see cref="DisconnectAsync"/> disarms both detectors first, so it reports a normal
/// disconnect and never a loss.
/// </para>
/// <para>
/// <b>Idle CPU on Unix.</b> A connected but completely silent device is not free on macOS or Linux.
/// .NET's Unix <c>SerialStream</c> services a pending read from a helper thread that alternates a
/// 1 ms sleep with <c>TIOCMGET</c>/<c>FIONREAD</c> ioctls, so it costs roughly <b>2% of one core
/// for as long as a read is outstanding</b>. This transport does not read on its own — an open port
/// with nobody reading it measures 0.0%. The cost arrives with whoever reads <see cref="Stream"/>:
/// a <see cref="Consumers.StreamMessageConsumer{T}"/> keeps a blocking read pending the whole time
/// it is running, which for a connected <see cref="Device.DaqifiStreamingDevice"/> is all of the
/// time except the brief windows a text exchange suspends it for. Measured over a 30 s idle window
/// on <c>System.IO.Ports</c> 10.0.11 (issue #673): 0.0% with the port merely open, 2.0% on macOS
/// and 2.3% on Linux with a blocking read pending, and about 3% for a whole connected device.
/// Callers holding many devices open for hours should budget for it. Windows drives reads through
/// overlapped I/O rather than a poll loop and so is not expected to pay it, but that has not been
/// measured.
/// </para>
/// <para>
/// Because the helper thread polls for the read's <i>duration</i>, the cost follows time spent
/// inside a read rather than the number of reads. So it is unaffected by
/// <see cref="OperationalReadTimeoutMs"/> (an infinite read timeout measured slightly worse, not
/// better), and unaffected by any reshaping of the consumer's loop that still leaves a blocking
/// read continuously pending. The one lever that does reach it is having no read pending while the
/// line is quiet: gating each read on <see cref="SerialPort.BytesToRead"/> — a bare <c>FIONREAD</c>
/// ioctl that does not start the helper thread — measures about 0.1%. That trade has deliberately
/// not been taken, because it buys the saving with added first-byte latency after an idle gap on a
/// path whose timing the SCPI exchanges depend on.
/// </para>
/// </remarks>
public class SerialStreamTransport : IStreamTransport, ITransportHealthSink
{
    /// <summary>
    /// Default cadence at which an established connection is checked for the continued presence of
    /// its serial port. Combined with the two consecutive misses required to act, an unplugged
    /// device is reported within roughly three seconds.
    /// </summary>
    public static readonly TimeSpan DefaultLivenessCheckInterval =
        TransportConnectionWatchdog.DefaultPresencePollInterval;

    private readonly string _portName;
    private readonly SerialTransportOptions _options;
    private readonly TransportConnectionWatchdog _watchdog;
    private SerialPort? _serialPort;

    // Signalled (and replaced) by every disconnect and dispose, to end the connects in flight. Never
    // disposed: it holds no timer or wait handle, and a late Cancel must stay safe.
    private CancellationTokenSource _teardown = new();

    // Makes "is this connect still wanted?" and publishing its handle one step against a teardown
    // taking that handle. Never held while anything blocking or any subscriber runs.
    private readonly Lock _handleLock = new();

    // The most recent open, which may have outlived the connect that started it. It never faults,
    // and by the time it completes a port nobody is waiting for any more has been closed.
    private Task _lastOpen = Task.CompletedTask;
    private volatile bool _disposed;

    /// <summary>
    /// Blocking-read timeout applied once the port is open. The connection timeout is only
    /// needed for retry/backoff logic, not for reads during normal operation; a short value
    /// keeps consumer threads stoppable (StopSafely). It is a shutdown-latency knob, not a CPU one
    /// — see the idle-CPU paragraph on the class for why changing it does not move idle cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This value <b>is</b> how long it takes to stop a reader, and nothing else. A
    /// <see cref="Consumers.StreamMessageConsumer{T}"/> checks its stop flag at the top of its
    /// loop and is otherwise parked inside <see cref="Stream.Read(byte[], int, int)"/>, and every
    /// stop path joins that thread — so a stop cannot return until the in-flight read gives up.
    /// At the 500 ms this used to be, the three text exchanges a connect runs each paid
    /// 482–499 ms to put the protobuf consumer down before they sent a single byte. Measured on a
    /// bench Nq1 running firmware 3.8.0: those three stops cost 49 / 34 / 32 ms at this value, a
    /// USB connect went from 3.48–3.57 s to 1.63–1.69 s, and <c>*IDN?</c> went from 1.05 s to
    /// 0.42 s (issue #900).
    /// </para>
    /// <para>
    /// A text exchange no longer stops the reader at all — it switches the reader's framing
    /// instead (issue #900) — so this value now bounds the stops that remain: a raw capture's
    /// (the SD download) and a disconnect's.
    /// </para>
    /// <para>
    /// It costs a little CPU, and the cost is small because of what the idle-CPU paragraph on this
    /// class measured: the Unix helper thread polls for a read's <i>duration</i>, so most of the
    /// idle cost follows time spent with a read pending rather than the number of reads issued —
    /// which is why an infinite read timeout measured slightly <i>worse</i> there rather than
    /// better. What a shorter timeout does add is the consumer loop's own per-read work, ten times
    /// as often on a line with nothing on it. Measured on a bench Nq1 over two channels, as CPU
    /// seconds across a 15 s window, two runs each way, alternated: at 5 kHz <b>11.0% of one core
    /// before, 11.8% after</b>; at 10 Hz (an all-but-silent link, where the timeout fires on
    /// nearly every read) <b>3.3% before, 4.2% after</b>. Sample counts were identical run for
    /// run, with no discards and no decode failures. Under one point of a core, for 1.9 s off a
    /// connect and 0.64 s off every query.
    /// </para>
    /// <para>
    /// It is the same value, chosen for the same reason, as
    /// <see cref="Device.Discovery.SerialDeviceFinder"/>'s post-identify read timeout: a reader
    /// that is about to be joined should be holding a read it can give up on quickly.
    /// </para>
    /// <para>
    /// One caller reads this stream directly rather than through a consumer, and it does depend on
    /// a long read: an SD file download treats a zero-length read as "the device went quiet" over
    /// USB serial, so the read timeout is its stall tolerance. That dependency used to be this
    /// constant, implicitly. It is now stated where it is relied on —
    /// <c>TextExchangeEngine.RawCaptureReadTimeoutMs</c> raises the timeout for the duration of a
    /// raw capture and restores it afterwards — so the two can move independently.
    /// </para>
    /// </remarks>
    private const int OperationalReadTimeoutMs = 50;

    /// <summary>
    /// Blocking-write timeout applied once the port is open. Everything written over this
    /// transport is a short SCPI command line, so a healthy device drains a write in
    /// milliseconds. Only <see cref="SerialPort.ReadTimeout"/> used to be lowered after open,
    /// leaving writes bounded by the caller's <see cref="ConnectionRetryOptions.ConnectionTimeout"/>
    /// for the life of the port — a value chosen for retry/backoff, not for how long a command
    /// write may legitimately take, and one a consumer can set arbitrarily high. Since
    /// <see cref="SerialPort.Write(byte[], int, int)"/> accepts no <see cref="CancellationToken"/>,
    /// nothing else can shorten that wait on a device that has stopped draining its receive
    /// buffer (#399).
    /// </summary>
    private const int OperationalWriteTimeoutMs = 2000;

    /// <summary>
    /// Test seam: replaces the "is this port still enumerated?" probe so the liveness check can be
    /// exercised without unplugging real hardware. Never set in production.
    /// </summary>
    internal Func<string, bool>? PortPresenceProbe { get; set; }

    /// <summary>
    /// Test seam: replaces <see cref="SerialPort.Open"/> so an open that hangs in native code can be
    /// reproduced without a wedged port. Never set in production.
    /// </summary>
    internal Action<SerialPort>? PortOpener { get; set; }

    /// <summary>
    /// Initializes a new instance of the SerialStreamTransport class.
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM1", "/dev/ttyUSB0").</param>
    /// <param name="options">How to open and watch the port, or <see langword="null"/> for the defaults.</param>
    public SerialStreamTransport(string portName, SerialTransportOptions? options = null)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _options = options ?? new SerialTransportOptions();

        _watchdog = new TransportConnectionWatchdog(
            $"Serial transport ({_portName})",
            HandleConnectionLost);
    }

    /// <summary>
    /// Test seam: injects the underlying <see cref="SerialPort"/> so the closed-port
    /// stream-access path (issue #238) can be exercised without real hardware — e.g. by
    /// passing a constructed-but-unopened port whose <see cref="SerialPort.IsOpen"/> is
    /// <c>false</c>. The transport takes ownership: any previously held port is disposed when
    /// replaced or cleared, and the current port is disposed on <see cref="Dispose"/>. Never
    /// used in production.
    /// </summary>
    /// <param name="serialPort">The serial port to use, or <c>null</c> to clear it.</param>
    internal void SetSerialPortForTesting(SerialPort? serialPort)
    {
        if (ReferenceEquals(_serialPort, serialPort))
        {
            return;
        }

        _serialPort?.Dispose();
        _serialPort = serialPort;
    }

    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    /// <exception cref="TransportNotConnectedException">
    /// Thrown when the serial port is not open — either never connected, or closed mid-operation
    /// by a device unplug or a DTR-triggered MCU reset that re-enumerated the COM port.
    /// </exception>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();

            // Capture the field into a local so the check-then-access below is stable: a
            // concurrent DisconnectAsync()/Dispose() nulls _serialPort in a finally with no
            // synchronization, which would otherwise turn the BaseStream dereference into a
            // NullReferenceException instead of the intended typed signal.
            var port = _serialPort;

            // Guard on IsOpen (not just non-null) before touching BaseStream. When the port is
            // non-null but closed — the device was unplugged, or a DTR-triggered MCU reset
            // re-enumerated the COM port mid-connect — SerialPort.BaseStream's getter itself
            // throws a raw InvalidOperationException ("The BaseStream is only available when the
            // port is open.") that reads like an app bug. Surface a typed, transport-state
            // exception instead so consumers can classify a dropped transport as a transient,
            // environmental condition (issue #238; serial analog of #237).
            if (port?.IsOpen != true)
            {
                throw new TransportNotConnectedException(
                    $"Serial transport is not connected ({_portName}).");
            }

            try
            {
                return port.BaseStream;
            }
            catch (InvalidOperationException ex)
            {
                // The port can close between the IsOpen check above and this getter — the exact
                // unplug / DTR-reset race #238 is about. SerialPort.BaseStream's only
                // InvalidOperationException is the "only available when the port is open" case,
                // so translate it to the typed signal (preserving the original as InnerException)
                // rather than leaking the raw framework message.
                throw new TransportNotConnectedException(
                    $"Serial transport is not connected ({_portName}).", ex);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    public bool IsConnected => _serialPort?.IsOpen == true;

    /// <summary>
    /// The port this transport was constructed for, for example <c>COM7</c> or
    /// <c>/dev/cu.usbmodem1101</c>.
    /// </summary>
    /// <remarks>
    /// The name an external program needs, on its own. <see cref="ConnectionInfo"/> carries the
    /// same port inside a sentence for a log line, so it cannot be passed to a tool. The port is
    /// fixed at construction and does not change, so this answers the same whether the transport
    /// is open or closed.
    /// </remarks>
    internal string PortName => _portName;

    /// <summary>
    /// Always <c>true</c>: a serial transport is the device's USB CDC link.
    /// </summary>
    public bool IsUsbConnection => true;

    /// <summary>
    /// Gets information about the transport connection.
    /// </summary>
    public string ConnectionInfo
    {
        get
        {
            if (!IsConnected)
                return $"Serial: Disconnected ({_portName})";

            return $"Serial: {_portName} @ {_options.BaudRate} baud";
        }
    }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Cancellation is observed between attempts, while waiting out a backoff delay, and while the
    /// port is being opened. The framework offers no cancellable <see cref="SerialPort.Open"/>, and
    /// on a wedged port it can stay in native code indefinitely, so the open runs on its own thread
    /// and a cancel abandons it: this method throws at once, and the port is closed whenever the
    /// open finally returns. An open that had already begun may still pulse DTR.
    /// </para>
    /// <para>
    /// The transport can be connected again afterwards. Because the abandoned open may still hold
    /// the port, each attempt first waits for it to finish, for at most
    /// <see cref="ConnectionRetryOptions.ConnectionTimeout"/>.
    /// </para>
    /// <para>
    /// <see cref="DisconnectAsync"/> and <see cref="Dispose"/> end a connect in flight the same way,
    /// retries included.
    /// </para>
    /// <para>
    /// A failed <see cref="SerialPort.Open"/> is reported as a
    /// <see cref="SerialPortConnectException"/> naming the port and why it could not be opened,
    /// rather than the platform's own exception, which on macOS and Linux calls every failure —
    /// including a port that simply does not exist — an access denial (#424). The original is kept
    /// as the inner exception. The reason is decided from what can be observed at the moment of
    /// the failure: whether the port is still present, and whether the platform could be applying
    /// a permission gate at all. Retry behavior is unchanged; a translated failure is still one
    /// failed attempt.
    /// </para>
    /// <para>
    /// <b>Behavioral change for callers.</b> This method used to surface whatever
    /// <see cref="SerialPort.Open"/> threw — on macOS and Linux always an
    /// <see cref="UnauthorizedAccessException"/> whatever the real cause, on Windows a
    /// <see cref="FileNotFoundException"/> or an <see cref="UnauthorizedAccessException"/>. Those
    /// are now wrapped, so <c>catch (UnauthorizedAccessException)</c> around a connect no longer
    /// matches and any branching on the platform exception's type has to move. Catch
    /// <see cref="SerialPortConnectException"/> and switch on
    /// <see cref="SerialPortConnectException.Reason"/>, which is the classification the platform
    /// exception never reliably carried; <c>catch (IOException)</c> still matches for callers that
    /// only need the broad case. The original exception remains available as
    /// <see cref="Exception.InnerException"/>, so nothing is lost — only the type that arrives at
    /// the catch site changes.
    /// </para>
    /// </remarks>
    /// <exception cref="SerialPortConnectException">
    /// Thrown when the final attempt cannot open the port, with
    /// <see cref="SerialPortConnectException.Reason"/> naming the cause.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the final attempt could not start because an earlier open, abandoned by a cancel,
    /// still had not returned.
    /// </exception>
    /// <exception cref="TransportNotConnectedException">
    /// Thrown when the transport was disconnected while it was connecting.
    /// </exception>
    public async Task ConnectAsync(
        ConnectionRetryOptions? retryOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Read before the disposed check, so a dispose that this check misses is one whose signal
        // this connect sees.
        var teardownToken = Volatile.Read(ref _teardown).Token;
        ThrowIfDisposed();

        if (IsConnected)
            return;

        // Retire the previous session's drop detection before any new handle is published: a
        // reconnect straight over a dead session (no Disconnect in between) would otherwise leave
        // it armed. StartDropDetection() re-arms once the connect succeeds.
        _watchdog.Disarm();

        // The port this connect published, so cleaning up after it can never take down the port of a
        // connect that has replaced it.
        SerialPort? published = null;

        await ConnectRetryExecutor.ExecuteAsync(
            retryOptions,
            connectAttempt: async (options, attemptToken) =>
            {
                // A dead port still held from the previous session is released, not overwritten.
                SerialPort? dead;
                lock (_handleLock)
                {
                    attemptToken.ThrowIfCancellationRequested();
                    (dead, _serialPort) = (_serialPort, null);
                }

                dead?.Dispose();

                await WaitForLastOpenAsync(options.ConnectionTimeout, attemptToken).ConfigureAwait(false);

                // Last chance to bail before the open starts — on a serial port a DTR-triggered MCU
                // reset also fires here, so not opening at all is the only way to honor a cancel
                // that arrived while the previous attempt was backing off.
                attemptToken.ThrowIfCancellationRequested();

                var timeout = (int)options.ConnectionTimeout.TotalMilliseconds;
                var port = new SerialPort(_portName, _options.BaudRate, Parity.None, dataBits: 8, StopBits.One)
                {
                    ReadTimeout = timeout,
                    WriteTimeout = timeout,
                    DtrEnable = _options.EnableDtr,
                    RtsEnable = _options.EnableRts
                };

                await OpenAsync(port, attemptToken).ConfigureAwait(false);

                // After a successful open, swap the connect timeouts for the (shorter)
                // operational ones — both directions, not just reads (#399).
                ApplyOperationalTimeouts(port);

                // Published and armed in one step, before the executor announces the connection: a
                // StatusChanged(true) subscriber may disconnect (which disarms) or reconnect
                // (which arms its own session) synchronously, and arming afterwards would act on
                // whatever the field holds by then.
                lock (_handleLock)
                {
                    if (!attemptToken.IsCancellationRequested)
                    {
                        _serialPort = published = port;
                        _watchdog.Arm(port);
                    }
                }

                if (!ReferenceEquals(published, port))
                {
                    DisposeQuietly(port);
                    attemptToken.ThrowIfCancellationRequested();
                }

                StartPresencePolling();
            },
            onAttemptFailed: () =>
            {
                // An attempt can fail after it armed detection for its port (a StatusChanged(true)
                // subscriber that throws fails the attempt), so the failed attempt's detection and
                // presence polling are retired with its port.
                lock (_handleLock)
                {
                    if (published is not null && ReferenceEquals(_serialPort, published))
                    {
                        _watchdog.Disarm();
                        _serialPort = null;
                    }
                }

                published?.Dispose();
                published = null;
            },
            onStatusChanged: OnStatusChanged,
            cancellationToken,
            teardownToken,
            CreateTornDownException).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="port"/> on its own thread, giving up on it when
    /// <paramref name="cancellationToken"/> is signalled. On return the port is open and the
    /// caller's; on any throw it is closed, now or as soon as the abandoned open lets go of it.
    /// </summary>
    private async Task OpenAsync(SerialPort port, CancellationToken cancellationToken)
    {
        const int pending = 0, returned = 1, abandoned = 2;
        var handoff = pending;

        // LongRunning: an open can block for as long as the driver likes, and must not hold a
        // pool thread (or the caller's, which may be a UI thread) while it does.
        var open = Task.Factory.StartNew<Exception?>(
            () =>
            {
                Exception? failure = null;
                try
                {
                    if (PortOpener is { } opener)
                    {
                        opener(port);
                    }
                    else
                    {
                        port.Open();
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                // Whoever is last to touch the port closes it. Closing it from the connect that
                // gave up would race the open, which would then leave its handle behind in a port
                // nobody holds.
                if (Interlocked.CompareExchange(ref handoff, returned, pending) == abandoned)
                {
                    DisposeQuietly(port);
                }

                return failure;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _lastOpen = open;

        Exception? error;
        try
        {
            error = await open.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref handoff, abandoned, pending) == returned)
            {
                DisposeQuietly(port);
            }

            throw;
        }

        if (error is null)
        {
            return;
        }

        DisposeQuietly(port);

        if (error is UnauthorizedAccessException or IOException)
        {
            // Name the failure instead of forwarding the platform's guess at it (#424).
            // The evidence is gathered here, immediately after the failed open, because it
            // is only meaningful in that moment; the classification itself lives in
            // SerialPortConnectException.Classify. Argument and state exceptions are left
            // alone — a bad baud rate or an already-open port is a caller bug, not a port
            // that could not be opened.
            throw SerialPortConnectException.FromOpenFailure(
                _portName, error, TryObservePortPresence(), TryRuleOutPermissionGate());
        }

        ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>
    /// Waits for an open abandoned by an earlier connect to let go of the port, so a reconnect does
    /// not open it a second time alongside the first.
    /// </summary>
    private async Task WaitForLastOpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _lastOpen.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Serial port {_portName} is still being opened by an earlier connect that was abandoned; "
                + $"it did not finish within {timeout.TotalSeconds:0.###}s.", ex);
        }
    }

    /// <summary>
    /// Ends every connect in flight, including one waiting out a backoff. Called first by every
    /// teardown, so a connect cannot redial or publish a handle after the caller has closed the
    /// transport; a connect started afterwards gets the fresh source and is unaffected.
    /// </summary>
    private void EndConnectsInFlight() =>
        Interlocked.Exchange(ref _teardown, new CancellationTokenSource()).Cancel();

    /// <summary>What a connect ended by a disconnect or a dispose throws.</summary>
    private Exception CreateTornDownException(Exception cause) =>
        _disposed
            ? new ObjectDisposedException(nameof(SerialStreamTransport))
            : new TransportNotConnectedException(
                $"Serial transport was disconnected while it was connecting ({_portName}).", cause);

    private static void DisposeQuietly(SerialPort port)
    {
        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // The connect has already failed or been given up; closing the handle is best effort.
        }
    }

    /// <summary>
    /// Replaces the connect-phase timeouts with the operational ones on an opened port.
    /// Internal (not inlined at the call site) so the values can be asserted without a real
    /// port: <see cref="SerialPort.ReadTimeout"/>/<see cref="SerialPort.WriteTimeout"/> are
    /// settable while the port is closed and are carried into the handle on open.
    /// </summary>
    /// <param name="port">The port to configure.</param>
    internal static void ApplyOperationalTimeouts(SerialPort port)
    {
        port.ReadTimeout = OperationalReadTimeoutMs;
        port.WriteTimeout = OperationalWriteTimeoutMs;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Close();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Closes the connection. Shared by <see cref="DisconnectAsync"/> and <see cref="Dispose"/>.
    /// </summary>
    private void Close()
    {
        EndConnectsInFlight();

        // Disarm before touching the port. Closing it makes any in-flight read fail, and the
        // port stops being "present" the moment it is released, so a still-armed watchdog could
        // report an intentional disconnect as a lost connection.
        SerialPort? port;
        lock (_handleLock)
        {
            _watchdog.Disarm();
            (port, _serialPort) = (_serialPort, null);
        }

        // Gated on holding a port, not on IsConnected: a port that closed underneath us (unplug,
        // DTR reset re-enumeration) reads IsOpen == false while still held, and returning early
        // on it would leave it held with the watchdog already disarmed.
        if (port is null)
            return;

        try
        {
            port.Close();
        }
        catch (Exception ex)
        {
            OnStatusChanged(false, ex);
            throw;
        }
        finally
        {
            port.Dispose();
            OnStatusChanged(false, null);
        }
    }

    /// <summary>
    /// Gets the available serial port names on the system.
    /// </summary>
    /// <returns>An array of available port names.</returns>
    public static string[] GetAvailablePortNames()
    {
        return SerialPort.GetPortNames();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Escalates to a lost connection only after five failures with no successful transfer in
    /// between, so a transient read error cannot tear down a healthy port.
    /// </remarks>
    public void ReportIoFault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _watchdog.RecordFault(error);
    }

    /// <inheritdoc />
    public void ReportIoSuccess()
    {
        _watchdog.RecordSuccess();
    }

    /// <summary>
    /// Gets a value indicating whether port-presence polling is currently running. Exposed for
    /// tests and diagnostics; polling is skipped when the interval is
    /// <see cref="TimeSpan.Zero"/> or the port is not visible to the presence probe at connect time.
    /// </summary>
    internal bool IsLivenessMonitorActive => _watchdog.IsPollingPresence;

    /// <summary>
    /// Test seam: runs one port-presence check immediately instead of waiting for the timer.
    /// </summary>
    internal void PollLivenessForTesting() => _watchdog.PollPresence();

    /// <summary>
    /// Arms drop detection after a successful open and, when possible, starts port-presence polling.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the arming decision and the presence-driven loss can be
    /// exercised without a real port to unplug (paired with <see cref="PortPresenceProbe"/>).
    /// </remarks>
    internal void StartDropDetection()
    {
        _watchdog.Arm(_serialPort!);
        StartPresencePolling();
    }

    /// <summary>
    /// Starts port-presence polling for the armed session, when the probe can see the port.
    /// </summary>
    private void StartPresencePolling()
    {
        if (_options.LivenessCheckInterval <= TimeSpan.Zero)
        {
            return;
        }

        // Only arm the presence check if the probe can actually see the port we just opened.
        // If a platform enumerates it under a different spelling than the caller passed, every
        // poll would read as "gone" and would disconnect a perfectly healthy connection — far
        // worse than not having the check. Fault escalation still covers that case.
        //
        // A probe that throws here is treated the same way: without a baseline observation there
        // is nothing to compare later polls against, so the check stays off rather than guessing.
        // The throw must not escape either — the port is open and the connect succeeded.
        try
        {
            if (!IsPortPresent())
            {
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        _watchdog.StartPresencePolling(
            IsPortPresent,
            $"Serial port {_portName} is no longer present; the device appears to have been disconnected.",
            _options.LivenessCheckInterval);
    }

    /// <summary>
    /// Observes whether the port exists, for classifying a failed open. Returns <c>null</c> when
    /// the probe could not answer — a failure to observe is not evidence of absence, and reporting
    /// it as absence would turn an unrelated connect failure into a bogus "port was not found".
    /// </summary>
    private bool? TryObservePortPresence()
    {
        try
        {
            return IsPortPresent();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports whether the platform can be ruled out as the source of an access-denied failure,
    /// which is what separates a port held by another process from one the caller may not open.
    /// </summary>
    /// <returns>
    /// <c>true</c> when no per-user permission gate can apply, <c>false</c> when one could,
    /// <c>null</c> when it could not be determined.
    /// </returns>
    /// <remarks>
    /// A Unix device node that grants read and write to every user cannot produce a permission
    /// failure for anybody, so a denial on such a node is exclusivity — the case macOS reports with
    /// the same exception it uses for every other open failure, and <c>/dev/cu.*</c> nodes there are
    /// <c>crw-rw-rw-</c>. A node that does not grant that (a Linux <c>dialout</c>-owned port at
    /// <c>crw-rw----</c>, say) keeps the access-denied reading, so the genuine permission case is
    /// still reported as one. Windows COM ports have no comparable gate: a port that exists and
    /// refuses to open is one another process holds.
    /// </remarks>
    private bool? TryRuleOutPermissionGate()
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(_portName);
            return mode.HasFlag(UnixFileMode.OtherRead) && mode.HasFlag(UnixFileMode.OtherWrite);
        }
        catch (Exception)
        {
            // No stat (the port is gone, the name is not a path, or the platform will not say).
            // Unknown, never a guess: the caller falls back to the access-denied wording.
            return null;
        }
    }

    /// <summary>
    /// Reports whether the configured port is still present on the system.
    /// </summary>
    private bool IsPortPresent()
    {
        var probe = PortPresenceProbe;
        return probe != null ? probe(_portName) : IsPortEnumerated(_portName);
    }

    /// <summary>
    /// Default presence probe: the port is present if the framework enumerates it, or — on Unix,
    /// where a port name is a device node path — if that node still exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately requires <em>both</em> answers to be "absent" before reporting absence. The
    /// enumeration is the portable signal (and the only one on Windows); the filesystem check
    /// covers a Unix port whose spelling the enumeration happens not to return.
    /// </para>
    /// <para>
    /// A failure to observe is <b>not</b> absence, and this method must never conflate the two: a
    /// probe that cannot answer throws, and the caller treats that as no evidence of a drop.
    /// Returning <c>false</c> when the enumeration merely failed would let two transient failures
    /// look like two consecutive misses and close a healthy connection. A failed enumeration is
    /// therefore only swallowed when the filesystem check can still answer for this port name.
    /// </para>
    /// <para>
    /// The two sources are OR-ed, so the order they run in cannot change the answer — and the
    /// filesystem check goes first because it is by far the cheaper one. On Unix, where the port
    /// name <em>is</em> a device node path, that single stat answers the overwhelmingly common case
    /// (the port is still there) outright, and the system-wide enumeration never runs at all. When
    /// it does run it comes from <see cref="SerialPortNameSnapshot"/>, one snapshot shared by every
    /// polling transport in the process (issue #491).
    /// </para>
    /// </remarks>
    /// <exception cref="Exception">
    /// Propagates whatever the underlying probe raised when no source could answer.
    /// </exception>
    internal static bool IsPortEnumerated(string portName) =>
        IsPortEnumerated(portName, SerialPortNameSnapshot.Shared);

    /// <inheritdoc cref="IsPortEnumerated(string)"/>
    /// <param name="portName">The port name to probe.</param>
    /// <param name="snapshot">
    /// The enumeration cache to consult. Tests pass their own so the ordering can be asserted
    /// without touching the process-wide instance.
    /// </param>
    internal static bool IsPortEnumerated(string portName, SerialPortNameSnapshot snapshot)
    {
        var isDeviceNodePath = portName.StartsWith('/');

        if (isDeviceNodePath && File.Exists(portName))
        {
            return true;
        }

        try
        {
            return snapshot.Contains(portName);
        }
        catch (Exception) when (isDeviceNodePath)
        {
            // Enumeration can fail transiently (a /dev scan racing a device change). A Unix port
            // name is also a filesystem path, so an independent answer is still available — the
            // failure is only swallowed because that second source can actually answer. When it
            // cannot (a Windows-style name), the exception propagates as "no observation". Here
            // that second source has already spoken: the device node check above ran first and did
            // not find it.
            return false;
        }
    }

    /// <summary>
    /// Tears down a connection that was detected as dropped and reports it as a loss.
    /// </summary>
    /// <param name="error">The condition that identified the drop.</param>
    /// <param name="session">The port the watchdog was armed for.</param>
    /// <remarks>
    /// Internal so a test can deliver a loss for a port that has since been replaced.
    /// </remarks>
    internal void HandleConnectionLost(Exception error, object session)
    {
        // Only the port the loss was detected on is taken down. A loss that arrives after a
        // reconnect replaced it (the detector was already running when the reconnect began) finds
        // a different port here and leaves the new connection alone; the old port was already
        // released by the reconnect.
        //
        // Dropping the reference comes before anything that can block: IsConnected must already
        // read false when StatusChanged fires, so a handler (or the device registry's pruning)
        // sees the truth. Closing a port whose device has physically vanished is the one call here
        // that can stall, so it happens after the notification.
        var port = (SerialPort)session;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _serialPort, null, port), port))
        {
            return;
        }

        try
        {
            OnStatusChanged(false, error);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not cost us the port handle (issue #494). The reference
            // has already been taken out of the field, so if this unwound past the dispose below,
            // DisconnectAsync() and Dispose() would both find null and skip it too — the OS port would
            // stay claimed for the life of the process and re-plugging the device would fail with
            // "Access is denied". Not rethrown: the callers of this are a watchdog timer thread and
            // the reader/writer loops, all of which already absorb it, so propagating only risks
            // disturbing them. A DaqifiDevice surfaces its own StateChanged subscriber failures on
            // ErrorOccurred; this transport carries no logger, so a consumer working against a bare
            // transport gets the same best-effort trace DeviceFinderBase gives its event raises.
            //
            // The lambda is load-bearing, not decoration: `ex` came out of consumer code too, so
            // rendering it is no safer than raising to it. Composing the line eagerly — SafeTrace($"...")
            // — would let a throwing ToString() escape the very catch that is containing the
            // subscriber, and the watchdog/reader thread would be disrupted anyway (issue #494,
            // pinned by DropPathSubscriberIsolationTests).
            SafeTrace(() =>
                $"[{nameof(SerialStreamTransport)}] a {nameof(StatusChanged)} subscriber threw while a dropped connection was being reported: {ex}");
        }
        finally
        {
            try
            {
                port?.Dispose();
            }
            catch (Exception)
            {
                // The device is already gone; failing to close its handle changes nothing.
            }
        }
    }

    /// <summary>
    /// Raises the StatusChanged event.
    /// </summary>
    /// <param name="isConnected">The current connection status.</param>
    /// <param name="error">Any error that occurred, if applicable.</param>
    protected virtual void OnStatusChanged(bool isConnected, Exception? error)
    {
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(isConnected, ConnectionInfo, error));
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SerialStreamTransport));
    }

    /// <summary>
    /// Disposes the transport and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            // Set before connects in flight are ended, so they see why, and no connect can start after.
            _disposed = true;

            try
            {
                Close();
            }
            catch
            {
                // Ignore errors during disposal
            }

            _watchdog.Dispose();
            _serialPort?.Dispose();
        }
    }
}