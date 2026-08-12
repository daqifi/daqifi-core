using System.IO.Ports;

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
/// <see cref="Device.DaqifiDevice"/> surfaces as <see cref="Device.ConnectionStatus.Lost"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Port-presence polling</b> at <see cref="DefaultLivenessCheckInterval"/> (1 s), requiring two
/// consecutive misses. This bounds detection of an unplug at <b>roughly three seconds</b> even on
/// a completely idle connection. Presence is <see cref="SerialPort.GetPortNames"/> containing the
/// port, falling back to the device node still existing on Unix — no WMI, so it behaves
/// identically on Windows, macOS, and Linux. It is armed only if the port is visible to that probe
/// at connect time, so a platform or port-name spelling the probe cannot see disables the check
/// rather than reporting a false drop.
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
/// An intentional <see cref="Disconnect"/> disarms both detectors first, so it reports a normal
/// disconnect and never a loss.
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
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private readonly bool _enableDtr;
    private readonly bool _enableRts;
    private readonly TimeSpan _livenessCheckInterval;
    private readonly TransportConnectionWatchdog _watchdog;
    private SerialPort? _serialPort;
    private bool _disposed;

    /// <summary>
    /// Blocking-read timeout applied once the port is open. The connection timeout is only
    /// needed for retry/backoff logic, not for reads during normal operation; a short value
    /// keeps consumer threads stoppable (StopSafely).
    /// </summary>
    private const int OperationalReadTimeoutMs = 500;

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
    /// Initializes a new instance of the SerialStreamTransport class.
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM1", "/dev/ttyUSB0").</param>
    /// <param name="baudRate">The baud rate for the connection.</param>
    /// <param name="parity">The parity setting.</param>
    /// <param name="dataBits">The number of data bits.</param>
    /// <param name="stopBits">The stop bits setting.</param>
    /// <param name="enableDtr">Whether to enable Data Terminal Ready (DTR) signal. Default is true.</param>
    /// <param name="enableRts">Whether to enable Request To Send (RTS) signal. Default is false.</param>
    /// <param name="livenessCheckInterval">
    /// How often an established connection re-checks that its port is still present. Defaults to
    /// <see cref="DefaultLivenessCheckInterval"/>; pass <see cref="TimeSpan.Zero"/> to disable the
    /// check and rely on I/O fault escalation alone.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="livenessCheckInterval"/> is negative.
    /// </exception>
    public SerialStreamTransport(string portName, int baudRate = 9600, Parity parity = Parity.None,
        int dataBits = 8, StopBits stopBits = StopBits.One, bool enableDtr = true, bool enableRts = false,
        TimeSpan? livenessCheckInterval = null)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
        _enableDtr = enableDtr;
        _enableRts = enableRts;

        _livenessCheckInterval = livenessCheckInterval ?? DefaultLivenessCheckInterval;
        if (_livenessCheckInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(livenessCheckInterval), _livenessCheckInterval,
                "Liveness check interval cannot be negative. Use TimeSpan.Zero to disable the check.");
        }

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
            // concurrent Disconnect()/Dispose() nulls _serialPort in a finally with no
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
    /// Gets information about the transport connection.
    /// </summary>
    public string ConnectionInfo
    {
        get
        {
            if (!IsConnected)
                return $"Serial: Disconnected ({_portName})";

            return $"Serial: {_portName} @ {_baudRate} baud";
        }
    }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Establishes the serial connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="SerialPortConnectException">
    /// Thrown when the port cannot be opened, with <see cref="SerialPortConnectException.Reason"/>
    /// naming the cause.
    /// </exception>
    public async Task ConnectAsync()
    {
        await ConnectAsync(null);
    }

    /// <summary>
    /// Establishes the serial connection asynchronously with retry support.
    /// </summary>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="SerialPortConnectException">
    /// Thrown when the final attempt cannot open the port, with
    /// <see cref="SerialPortConnectException.Reason"/> naming the cause.
    /// </exception>
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        await ConnectAsync(retryOptions, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Cancellation is observed between attempts and while waiting out a backoff delay, and is
    /// checked immediately before each <see cref="SerialPort.Open"/>. It cannot interrupt the
    /// <c>Open</c> call itself — the framework offers no cancellable form of it — so on a port that
    /// hangs open, cancellation takes effect when that call returns. In practice opening a serial
    /// port either succeeds or fails quickly; the long wait worth cancelling is the retry loop.
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
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsConnected)
            return;

        await ConnectRetryExecutor.ExecuteAsync(
            retryOptions,
            connectAttempt: (options, attemptToken) =>
            {
                var timeout = (int)options.ConnectionTimeout.TotalMilliseconds;
                _serialPort = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
                {
                    ReadTimeout = timeout,
                    WriteTimeout = timeout,
                    DtrEnable = _enableDtr,
                    RtsEnable = _enableRts
                };

                // Last chance to bail before an uninterruptible open — on a serial port a
                // DTR-triggered MCU reset also fires here, so not opening at all is the only way to
                // honor a cancel that arrived while the previous attempt was backing off.
                attemptToken.ThrowIfCancellationRequested();

                try
                {
                    _serialPort.Open();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Name the failure instead of forwarding the platform's guess at it (#424).
                    // The evidence is gathered here, immediately after the failed open, because it
                    // is only meaningful in that moment; the classification itself lives in
                    // SerialPortConnectException.Classify. Argument and state exceptions are left
                    // alone — a bad baud rate or an already-open port is a caller bug, not a port
                    // that could not be opened.
                    throw SerialPortConnectException.FromOpenFailure(
                        _portName, ex, TryObservePortPresence(), TryRuleOutPermissionGate());
                }

                // After a successful open, swap the connect timeouts for the (shorter)
                // operational ones — both directions, not just reads (#399).
                ApplyOperationalTimeouts(_serialPort);

                return Task.CompletedTask;
            },
            onAttemptFailed: () =>
            {
                _serialPort?.Dispose();
                _serialPort = null;
            },
            onStatusChanged: OnStatusChanged,
            cancellationToken: cancellationToken);

        StartDropDetection();
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

    /// <summary>
    /// Closes the serial connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    public async Task DisconnectAsync()
    {
        // Disarm before touching the port. Closing it makes any in-flight read fail, and the
        // port stops being "present" the moment it is released, so a still-armed watchdog could
        // report an intentional disconnect as a lost connection.
        _watchdog.Disarm();

        if (!IsConnected)
            return;

        try
        {
            _serialPort?.Close();
        }
        catch (Exception ex)
        {
            OnStatusChanged(false, ex);
            throw;
        }
        finally
        {
            _serialPort?.Dispose();
            _serialPort = null;
            OnStatusChanged(false, null);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Establishes the serial connection synchronously.
    /// </summary>
    public void Connect()
    {
        ConnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Closes the serial connection synchronously.
    /// </summary>
    public void Disconnect()
    {
        DisconnectAsync().GetAwaiter().GetResult();
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
        _watchdog.Arm();

        if (_livenessCheckInterval <= TimeSpan.Zero)
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
            _livenessCheckInterval);
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
    /// </remarks>
    /// <exception cref="Exception">
    /// Propagates whatever the underlying probe raised when no source could answer.
    /// </exception>
    internal static bool IsPortEnumerated(string portName)
    {
        var isDeviceNodePath = portName.StartsWith('/');

        try
        {
            foreach (var name in SerialPort.GetPortNames())
            {
                if (string.Equals(name, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception) when (isDeviceNodePath)
        {
            // Enumeration can fail transiently (a /dev scan racing a device change). A Unix port
            // name is also a filesystem path, so an independent answer is still available — the
            // failure is only swallowed because that second source can actually answer. When it
            // cannot (a Windows-style name), the exception propagates as "no observation".
            return File.Exists(portName);
        }

        // The enumeration answered and did not list this port. On Unix it may simply not enumerate
        // the spelling the caller opened, so the device node is the tie-breaker.
        return isDeviceNodePath && File.Exists(portName);
    }

    /// <summary>
    /// Tears down a connection that was detected as dropped and reports it as a loss.
    /// </summary>
    /// <param name="error">The condition that identified the drop.</param>
    private void HandleConnectionLost(Exception error)
    {
        // Drop the reference before anything that can block: IsConnected must already read false
        // when StatusChanged fires, so a handler (or the device registry's pruning) sees the truth.
        // Closing a port whose device has physically vanished is the one call here that can stall,
        // so it happens after the notification.
        var port = Interlocked.Exchange(ref _serialPort, null);

        try
        {
            OnStatusChanged(false, error);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not cost us the port handle (issue #494). The reference
            // has already been taken out of the field, so if this unwound past the dispose below,
            // Disconnect() and Dispose() would both find null and skip it too — the OS port would
            // stay claimed for the life of the process and re-plugging the device would fail with
            // "Access is denied". Not rethrown: the callers of this are a watchdog timer thread and
            // the reader/writer loops, all of which already absorb it, so propagating only risks
            // disturbing them. A DaqifiDevice surfaces its own StatusChanged subscriber failures on
            // ErrorOccurred; this transport carries no logger, so a consumer working against a bare
            // transport gets the same best-effort trace DeviceFinderBase gives its event raises.
            SafeTrace(
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
    /// Writes a diagnostic line, swallowing anything a misbehaving
    /// <see cref="System.Diagnostics.TraceListener"/> throws.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Diagnostics.Trace"/> dispatches to listeners the consumer installed, so it
    /// is consumer code and can throw like any other. A listener throwing out of the <c>catch</c>
    /// that was containing a bad subscriber would defeat the containment and cost the port handle
    /// anyway. Same guarantee as <c>DeviceFinderBase.RaiseIsolated</c> and
    /// <c>DaqifiStreamingDevice.SafeTrace</c>; this transport has no <c>ILogger</c>, hence the local
    /// twin rather than a shared one.
    /// </remarks>
    /// <param name="message">The diagnostic line to write.</param>
    private static void SafeTrace(string message)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
        catch
        {
            // A trace listener that throws is not permitted to affect the drop path.
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
            try
            {
                Disconnect();
            }
            catch
            {
                // Ignore errors during disposal
            }

            _watchdog.Dispose();
            _serialPort?.Dispose();
            _disposed = true;
        }
    }
}