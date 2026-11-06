using System.Net;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Represents a UDP transport mechanism for broadcast and unicast communication.
/// Used primarily for device discovery and UDP-based messaging.
/// </summary>
/// <remarks>
/// Unused and scheduled for removal — see issue #924. This seam was added so UDP traffic could be
/// replaced in a test, but the library's own UDP code (<c>WiFiDeviceFinder</c> and
/// <c>MDnsDeviceFinder</c>) opens <see cref="System.Net.Sockets.UdpClient"/> directly and never
/// reaches it, and nothing outside the library implements it either.
/// </remarks>
[Obsolete("Unused: DAQiFi Core's own UDP discovery opens System.Net.Sockets.UdpClient directly "
          + "rather than through this seam, and nothing else implements it. Use "
          + "System.Net.Sockets.UdpClient for UDP, or IStreamTransport for a TCP/serial stream. "
          + "This type will be removed in a future major version (#924).")]
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
    /// <remarks>
    /// Handlers run synchronously on whatever thread raised the change, and no particular thread
    /// is guaranteed — it may be the caller's or a thread-pool thread. The library resumes its
    /// awaits off the caller's <see cref="SynchronizationContext"/> so its synchronous facades
    /// cannot deadlock a UI thread (issue #495), but that only declines the context, it does not
    /// force a thread hop. A UI consumer must marshal to its own dispatcher before touching
    /// controls.
    /// </remarks>
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
