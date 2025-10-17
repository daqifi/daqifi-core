using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// TCP implementation of IStreamTransport that provides stream-based communication
/// over TCP connections. Handles connection lifecycle and provides the underlying
/// NetworkStream for message producers and consumers.
/// </summary>
public class TcpStreamTransport : IStreamTransport
{
    private readonly IPEndPoint _endPoint;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TcpStreamTransport class.
    /// </summary>
    /// <param name="ipAddress">The IP address to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    public TcpStreamTransport(IPAddress ipAddress, int port)
    {
        _endPoint = new IPEndPoint(ipAddress, port);
    }

    /// <summary>
    /// Initializes a new instance of the TcpStreamTransport class.
    /// </summary>
    /// <param name="host">The hostname to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    public TcpStreamTransport(string host, int port)
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
    }

    /// <summary>
    /// Gets the hostname if provided instead of IP address.
    /// </summary>
    public string? Hostname { get; }

    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when not connected.</exception>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();
            return _networkStream ?? throw new InvalidOperationException("Transport is not connected.");
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
    public async Task ConnectAsync()
    {
        await ConnectAsync(null);
    }

    /// <summary>
    /// Establishes the TCP connection asynchronously with retry support.
    /// </summary>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        ThrowIfDisposed();

        if (IsConnected)
            return;

        var options = retryOptions ?? ConnectionRetryOptions.NoRetry;
        var maxAttempts = options.Enabled ? options.MaxAttempts : 1;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Calculate delay for this attempt
                if (attempt > 1)
                {
                    var delay = options.CalculateDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                }

                _tcpClient = new TcpClient();

                // Set timeouts from retry options or use defaults
                var timeout = (int)options.ConnectionTimeout.TotalMilliseconds;
                _tcpClient.ReceiveTimeout = timeout;
                _tcpClient.SendTimeout = timeout;

                // Add connection timeout to prevent long waits
                using var cts = new CancellationTokenSource(options.ConnectionTimeout);
                var connectTask = Hostname != null
                    ? _tcpClient.ConnectAsync(Hostname, _endPoint.Port)
                    : _tcpClient.ConnectAsync(_endPoint.Address, _endPoint.Port);

                await connectTask.WaitAsync(cts.Token);

                _networkStream = _tcpClient.GetStream();
                OnStatusChanged(true, null);
                return; // Success!
            }
            catch (Exception ex)
            {
                lastException = ex;
                _tcpClient?.Dispose();
                _tcpClient = null;
                _networkStream = null;

                // If this is not the last attempt and retry is enabled, continue
                if (attempt < maxAttempts && options.Enabled)
                {
                    OnStatusChanged(false, new Exception($"Connection attempt {attempt}/{maxAttempts} failed, retrying...", ex));
                    continue;
                }

                // Last attempt failed or retry disabled
                OnStatusChanged(false, ex);
                throw;
            }
        }

        // Should not reach here, but just in case
        OnStatusChanged(false, lastException);
        throw lastException ?? new InvalidOperationException("Connection failed after all retry attempts.");
    }

    /// <summary>
    /// Closes the TCP connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    public async Task DisconnectAsync()
    {
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

        await Task.CompletedTask;
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

            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _disposed = true;
        }
    }
}