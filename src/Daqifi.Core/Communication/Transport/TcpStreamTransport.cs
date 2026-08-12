using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// TCP implementation of IStreamTransport that provides stream-based communication
/// over TCP connections. Handles connection lifecycle and provides the underlying
/// NetworkStream for message producers and consumers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drop detection.</b> <see cref="TcpClient.Connected"/> describes the last completed operation
/// rather than the current link, and nothing used to raise <see cref="StatusChanged"/> for an
/// unexpected drop, so <see cref="Device.ConnectionStatus.Lost"/> was unreachable here too
/// (issue #382). Two things now cover it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>I/O fault escalation</b> via <see cref="ITransportHealthSink"/>. The reader loop reports
/// failed reads — including a zero-byte read, which on a socket means the peer closed — and five
/// consecutive failures with no successful transfer in between close the socket and raise
/// <see cref="StatusChanged"/> with <c>IsConnected == false</c>. A closed or reset connection is
/// therefore reported within a fraction of a second. A read that merely hits its timeout is not a
/// failure and is never reported.
/// </description></item>
/// <item><description>
/// <b>TCP keep-alive</b> (10 s idle, 3 s probes, 3 retries), so a link that is silently severed —
/// a device losing power or dropping off WiFi, where no FIN or RST ever arrives — still fails a
/// read instead of hanging forever. This bounds detection of a silent drop at roughly
/// <b>twenty seconds</b> of idleness.
/// </description></item>
/// </list>
/// <para>
/// An intentional <see cref="Disconnect"/> disarms detection first, so it reports a normal
/// disconnect and never a loss.
/// </para>
/// </remarks>
public class TcpStreamTransport : IStreamTransport, ITransportHealthSink
{
    /// <summary>
    /// Idle time, in seconds, before TCP keep-alive probing starts on an established connection.
    /// </summary>
    internal const int KeepAliveTimeSeconds = 10;

    /// <summary>
    /// Interval, in seconds, between TCP keep-alive probes once probing has started.
    /// </summary>
    internal const int KeepAliveIntervalSeconds = 3;

    /// <summary>
    /// Number of unanswered TCP keep-alive probes before the OS fails the connection. With
    /// <see cref="KeepAliveTimeSeconds"/> and <see cref="KeepAliveIntervalSeconds"/> this bounds
    /// detection of a silently severed link at roughly twenty seconds.
    /// </summary>
    internal const int KeepAliveRetryCount = 3;

    /// <summary>
    /// Socket receive timeout applied once the connection is established, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The connection timeout is only needed for the connect handshake and retry/backoff logic,
    /// not for blocking reads during normal operation. Leaving the (multi-second) connect timeout
    /// in place meant a <see cref="Consumers.StreamMessageConsumer{T}"/> reader thread parked in a
    /// blocking <see cref="NetworkStream.Read(byte[], int, int)"/> could not be joined inside the
    /// consumer-swap window used by the text-exchange path, which surfaced as a spurious
    /// "a previous consumer thread has not yet exited" failure on connect (issue #383).
    /// A short operational timeout bounds that wait, matching the serial transport's post-open
    /// <c>ReadTimeout = 500</c>.
    /// </remarks>
    internal const int OperationalReceiveTimeoutMs = 500;

    private readonly IPEndPoint _endPoint;
    private readonly IPAddress? _localInterface;
    private readonly TransportConnectionWatchdog _watchdog;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private bool _disposed;

    /// <summary>
    /// Test seam: substitutes the connect task so the timeout translation can be exercised
    /// deterministically (e.g., with a never-completing task) instead of relying on real
    /// network behavior. Never set in production code.
    /// </summary>
    internal Func<TcpClient, Task>? ConnectTaskFactory { get; set; }

    /// <summary>
    /// Initializes a new instance of the TcpStreamTransport class.
    /// </summary>
    /// <param name="ipAddress">The IP address to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="localInterface">
    /// Optional local interface address to bind the outbound socket to. When supplied, the TCP
    /// connection egresses on the specified NIC; required for multi-homed hosts where the OS
    /// would otherwise pick the wrong interface for the route.
    /// </param>
    public TcpStreamTransport(IPAddress ipAddress, int port, IPAddress? localInterface = null)
    {
        _endPoint = new IPEndPoint(ipAddress, port);
        _localInterface = localInterface;
        _watchdog = CreateWatchdog();
    }

    /// <summary>
    /// Initializes a new instance of the TcpStreamTransport class.
    /// </summary>
    /// <param name="host">The hostname to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="localInterface">
    /// Optional local interface address to bind the outbound socket to. When supplied, the TCP
    /// connection egresses on the specified NIC; required for multi-homed hosts where the OS
    /// would otherwise pick the wrong interface for the route.
    /// </param>
    public TcpStreamTransport(string host, int port, IPAddress? localInterface = null)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            _endPoint = new IPEndPoint(ipAddress, port);
        }
        else
        {
            // For hostname resolution, we'll resolve during connection
            _endPoint = new IPEndPoint(IPAddress.None, port);
            Hostname = host;
        }
        _localInterface = localInterface;
        _watchdog = CreateWatchdog();
    }

    /// <summary>
    /// Creates the drop detector shared by both constructors.
    /// </summary>
    private TransportConnectionWatchdog CreateWatchdog()
    {
        return new TransportConnectionWatchdog(
            $"TCP transport ({Hostname ?? _endPoint.Address.ToString()}:{_endPoint.Port})",
            HandleConnectionLost);
    }

    /// <summary>
    /// Gets the hostname if provided instead of IP address.
    /// </summary>
    public string? Hostname { get; }

    /// <summary>
    /// Gets the local interface address the outbound socket will bind to, or null to let the OS choose.
    /// </summary>
    public IPAddress? LocalInterface => _localInterface;

    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    /// <exception cref="TransportNotConnectedException">
    /// Thrown when the TCP connection has not been established or has dropped.
    /// </exception>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();
            // Surface the same typed exception as the serial transport so consumers can
            // classify a not-connected transport uniformly across transport types (issue #238).
            return _networkStream ?? throw new TransportNotConnectedException("TCP transport is not connected.");
        }
    }

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    public bool IsConnected => _tcpClient?.Connected == true && _networkStream != null;

    /// <summary>
    /// Gets information about the transport connection.
    /// </summary>
    public string ConnectionInfo
    {
        get
        {
            if (!IsConnected)
                return $"TCP: Disconnected ({_endPoint})";

            var localEndPoint = _tcpClient?.Client?.LocalEndPoint;
            return $"TCP: {localEndPoint} -> {_endPoint}";
        }
    }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Establishes the TCP connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when the connection attempt does not complete within the configured connection timeout.
    /// </exception>
    public async Task ConnectAsync()
    {
        await ConnectAsync(null).ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes the TCP connection asynchronously with retry support.
    /// </summary>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when the final connection attempt does not complete within
    /// <see cref="ConnectionRetryOptions.ConnectionTimeout"/>.
    /// </exception>
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        await ConnectAsync(retryOptions, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="TimeoutException">
    /// Thrown when the final connection attempt does not complete within
    /// <see cref="ConnectionRetryOptions.ConnectionTimeout"/>.
    /// </exception>
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsConnected)
            return;

        await ConnectRetryExecutor.ExecuteAsync(
            retryOptions,
            connectAttempt: async (options, attemptToken) =>
            {
                _tcpClient = _localInterface != null
                    ? new TcpClient(new IPEndPoint(_localInterface, 0))
                    : new TcpClient();

                // Set timeouts from retry options or use defaults
                var timeout = (int)options.ConnectionTimeout.TotalMilliseconds;
                _tcpClient.ReceiveTimeout = timeout;
                _tcpClient.SendTimeout = timeout;

                // Two independent reasons to stop waiting — the connect timeout and the caller's
                // token — are linked into one source so a caller can abandon a dial that would
                // otherwise sit here for the full timeout. Which one fired is disambiguated below,
                // because the two mean very different things to the caller.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
                cts.CancelAfter(options.ConnectionTimeout);

                var connectTask = ConnectTaskFactory != null
                    ? ConnectTaskFactory(_tcpClient)
                    : Hostname != null
                        ? _tcpClient.ConnectAsync(Hostname, _endPoint.Port)
                        : _tcpClient.ConnectAsync(_endPoint.Address, _endPoint.Port);

                try
                {
                    await connectTask.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                    when (cts.IsCancellationRequested && !attemptToken.IsCancellationRequested)
                {
                    // The timeout — not the caller — ended the wait, so surface the failure as what
                    // it actually is rather than a misleading TaskCanceledException
                    // (daqifi-desktop#517). A caller-driven cancel falls through this filter and
                    // propagates as OperationCanceledException, which is what callers expect from a
                    // token they signalled themselves.
                    throw new TimeoutException(
                        $"TCP connect to {Hostname ?? _endPoint.Address.ToString()}:{_endPoint.Port} " +
                        $"timed out after {options.ConnectionTimeout.TotalSeconds:0.###}s.", oce);
                }

                _networkStream = _tcpClient.GetStream();

                // After a successful connect, lower the receive timeout to a short operational
                // value so consumer threads blocked in a synchronous read can always be stopped
                // promptly (StopSafely). Only synchronous reads observe this; the async SD-card
                // download path uses ReadAsync, which ignores SO_RCVTIMEO and keeps its own
                // long timeout. See OperationalReceiveTimeoutMs.
                _tcpClient.ReceiveTimeout = OperationalReceiveTimeoutMs;

                EnableKeepAlive(_tcpClient);
            },
            onAttemptFailed: () =>
            {
                _tcpClient?.Dispose();
                _tcpClient = null;
                _networkStream = null;
            },
            onStatusChanged: OnStatusChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _watchdog.Arm();
    }

    /// <summary>
    /// Turns on TCP keep-alive so a silently severed link (device powered off, WiFi dropped) fails
    /// a read instead of blocking forever with no FIN or RST ever arriving.
    /// </summary>
    /// <remarks>
    /// The per-socket tuning knobs are supported on Windows, macOS, and Linux, but a platform or
    /// socket that rejects one must not fail the connect: keep-alive is a detection improvement,
    /// not a requirement, and I/O fault escalation still covers a link that produces read errors.
    /// </remarks>
    private static void EnableKeepAlive(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);
        }
        catch (Exception)
        {
            // Not every platform/socket accepts every knob. A connection without keep-alive tuning
            // is still a working connection.
        }
    }

    /// <summary>
    /// Closes the TCP connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    public async Task DisconnectAsync()
    {
        // Disarm before touching the socket: closing it makes any in-flight read fail, and a
        // still-armed watchdog would report an intentional disconnect as a lost connection.
        _watchdog.Disarm();

        if (!IsConnected)
            return;

        try
        {
            _networkStream?.Close();
            _tcpClient?.Close();
        }
        catch (Exception ex)
        {
            OnStatusChanged(false, ex);
            throw;
        }
        finally
        {
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _networkStream = null;
            _tcpClient = null;
            OnStatusChanged(false, null);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes the TCP connection synchronously.
    /// </summary>
    public void Connect()
    {
        ConnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Closes the TCP connection synchronously.
    /// </summary>
    public void Disconnect()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Escalates to a lost connection only after five failures with no successful transfer in
    /// between, so a transient read error cannot tear down a healthy socket.
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
    /// Tears down a connection that was detected as dropped and reports it as a loss.
    /// </summary>
    /// <param name="error">The condition that identified the drop.</param>
    private void HandleConnectionLost(Exception error)
    {
        // Drop the references before anything that can block: IsConnected must already read false
        // when StatusChanged fires, so a handler (or the device registry's pruning) sees the truth.
        var stream = Interlocked.Exchange(ref _networkStream, null);
        var client = Interlocked.Exchange(ref _tcpClient, null);

        try
        {
            OnStatusChanged(false, error);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not cost us the socket (issue #494). The references have
            // already been taken out of their fields, so if this unwound past the dispose below,
            // Disconnect() and Dispose() would both find null and skip them too, leaking the socket
            // for the life of the process. Not rethrown, and traced best-effort because this
            // transport carries no logger: see SerialStreamTransport.HandleConnectionLost.
            SafeTrace(ex);
        }
        finally
        {
            try
            {
                stream?.Dispose();
                client?.Dispose();
            }
            catch (Exception)
            {
                // The peer is already gone; failing to close the socket changes nothing.
            }
        }
    }

    /// <summary>
    /// Writes the diagnostic line for a <see cref="StatusChanged"/> subscriber failure, swallowing
    /// anything a misbehaving <see cref="System.Diagnostics.TraceListener"/> throws. See
    /// <c>SerialStreamTransport.SafeTrace</c> for why the trace itself — and the composition of its
    /// message from a consumer-supplied exception — has to be contained.
    /// </summary>
    /// <param name="subscriberFailure">The exception the subscriber threw.</param>
    private static void SafeTrace(Exception subscriberFailure)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine(
                $"[{nameof(TcpStreamTransport)}] a {nameof(StatusChanged)} subscriber threw while a dropped connection was being reported: {subscriberFailure}");
        }
        catch
        {
            // A trace listener — or an exception that cannot render itself — is not permitted to
            // affect the drop path.
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
            throw new ObjectDisposedException(nameof(TcpStreamTransport));
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
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _disposed = true;
        }
    }
}