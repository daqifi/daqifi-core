using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// UDP implementation of IUdpTransport that provides broadcast and unicast communication.
/// Handles UDP socket lifecycle and provides both async and sync methods.
/// </summary>
public class UdpTransport : IUdpTransport
{
    private readonly int _localPort;
    private UdpClient? _udpClient;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the UdpTransport class that listens on any available port.
    /// </summary>
    public UdpTransport() : this(0)
    {
    }

    /// <summary>
    /// Initializes a new instance of the UdpTransport class.
    /// </summary>
    /// <param name="localPort">The local port to bind to, or 0 for any available port.</param>
    public UdpTransport(int localPort)
    {
        _localPort = localPort;
    }

    /// <summary>
    /// Gets a value indicating whether the transport is open and ready to send/receive.
    /// </summary>
    public bool IsOpen => _udpClient != null && !_disposed;

    /// <summary>
    /// Gets information about the transport configuration.
    /// </summary>
    public string ConnectionInfo
    {
        get
        {
            if (!IsOpen)
                return $"UDP: Closed (LocalPort: {_localPort})";

            try
            {
                var localEndPoint = _udpClient?.Client?.LocalEndPoint;
                return $"UDP: Open ({localEndPoint})";
            }
            catch
            {
                return $"UDP: Open (LocalPort: {_localPort})";
            }
        }
    }

    /// <summary>
    /// Occurs when the transport status changes.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Opens the UDP transport for communication.
    /// </summary>
    /// <returns>A task representing the asynchronous open operation.</returns>
    public async Task OpenAsync()
    {
        ThrowIfDisposed();

        if (IsOpen)
            return;

        try
        {
            _udpClient = new UdpClient(_localPort);
            _udpClient.EnableBroadcast = true;

            // Set socket options for better performance
            _udpClient.Client.ReceiveTimeout = 5000;
            _udpClient.Client.SendTimeout = 5000;

            OnStatusChanged(true, null);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _udpClient?.Dispose();
            _udpClient = null;
            OnStatusChanged(false, ex);
            throw;
        }
    }

    /// <summary>
    /// Closes the UDP transport.
    /// </summary>
    /// <returns>A task representing the asynchronous close operation.</returns>
    public async Task CloseAsync()
    {
        if (!IsOpen)
            return;

        try
        {
            _udpClient?.Close();
        }
        catch (Exception ex)
        {
            OnStatusChanged(false, ex);
            throw;
        }
        finally
        {
            _udpClient?.Dispose();
            _udpClient = null;
            OnStatusChanged(false, null);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a UDP broadcast message to the specified port.
    /// </summary>
    /// <param name="data">The data to broadcast.</param>
    /// <param name="port">The destination port.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    public async Task SendBroadcastAsync(byte[] data, int port)
    {
        ThrowIfDisposed();

        if (!IsOpen)
            throw new InvalidOperationException("UDP transport is not open.");

        var broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, port);
        await _udpClient!.SendAsync(data, data.Length, broadcastEndPoint);
    }

    /// <summary>
    /// Sends a UDP unicast message to a specific endpoint.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <param name="endPoint">The destination endpoint.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    public async Task SendUnicastAsync(byte[] data, IPEndPoint endPoint)
    {
        ThrowIfDisposed();

        if (!IsOpen)
            throw new InvalidOperationException("UDP transport is not open.");

        await _udpClient!.SendAsync(data, data.Length, endPoint);
    }

    /// <summary>
    /// Receives a UDP message with an optional timeout.
    /// </summary>
    /// <param name="timeout">The timeout for the receive operation.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the received data and remote endpoint, or null if timeout.</returns>
    public async Task<(byte[] Data, IPEndPoint RemoteEndPoint)?> ReceiveAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsOpen)
            throw new InvalidOperationException("UDP transport is not open.");

        try
        {
            UdpReceiveResult result;

            if (timeout.HasValue)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout.Value);

                try
                {
                    result = await _udpClient!.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout or cancellation occurred
                    return null;
                }
            }
            else
            {
                result = await _udpClient!.ReceiveAsync(cancellationToken);
            }

            return (result.Buffer, result.RemoteEndPoint);
        }
        catch (SocketException)
        {
            // Socket errors (like timeout) return null
            return null;
        }
    }

    /// <summary>
    /// Opens the UDP transport synchronously.
    /// </summary>
    public void Open()
    {
        OpenAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Closes the UDP transport synchronously.
    /// </summary>
    public void Close()
    {
        CloseAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Raises the StatusChanged event.
    /// </summary>
    /// <param name="isOpen">The current open status.</param>
    /// <param name="error">Any error that occurred, if applicable.</param>
    protected virtual void OnStatusChanged(bool isOpen, Exception? error)
    {
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(isOpen, ConnectionInfo, error));
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UdpTransport));
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
                Close();
            }
            catch
            {
                // Ignore errors during disposal
            }

            _udpClient?.Dispose();
            _disposed = true;
        }
    }
}
