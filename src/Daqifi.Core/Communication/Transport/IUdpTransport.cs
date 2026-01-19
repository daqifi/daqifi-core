using System.Net;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Represents a UDP transport mechanism for broadcast and unicast communication.
/// Used primarily for device discovery and UDP-based messaging.
/// </summary>
public interface IUdpTransport : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the transport is open and ready to send/receive.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Gets information about the transport configuration.
    /// </summary>
    string ConnectionInfo { get; }

    /// <summary>
    /// Occurs when the transport status changes.
    /// </summary>
    event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Opens the UDP transport for communication.
    /// </summary>
    /// <returns>A task representing the asynchronous open operation.</returns>
    Task OpenAsync();

    /// <summary>
    /// Closes the UDP transport.
    /// </summary>
    /// <returns>A task representing the asynchronous close operation.</returns>
    Task CloseAsync();

    /// <summary>
    /// Sends a UDP broadcast message to the specified port.
    /// </summary>
    /// <param name="data">The data to broadcast.</param>
    /// <param name="port">The destination port.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    Task SendBroadcastAsync(byte[] data, int port);

    /// <summary>
    /// Sends a UDP broadcast message to a specific endpoint.
    /// </summary>
    /// <param name="data">The data to broadcast.</param>
    /// <param name="endPoint">The destination broadcast endpoint.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    Task SendBroadcastAsync(byte[] data, IPEndPoint endPoint);

    /// <summary>
    /// Sends a UDP unicast message to a specific endpoint.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <param name="endPoint">The destination endpoint.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    Task SendUnicastAsync(byte[] data, IPEndPoint endPoint);

    /// <summary>
    /// Receives a UDP message with an optional timeout.
    /// </summary>
    /// <param name="timeout">The timeout for the receive operation.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the received data and remote endpoint, or null if timeout.</returns>
    Task<(byte[] Data, IPEndPoint RemoteEndPoint)?> ReceiveAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the UDP transport synchronously.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes the UDP transport synchronously.
    /// </summary>
    void Close();
}
