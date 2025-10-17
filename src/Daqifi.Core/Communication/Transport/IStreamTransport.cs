namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Represents a transport mechanism that provides stream-based communication.
/// Abstracts the underlying connection type (TCP, UDP, Serial, etc.) and provides
/// a unified Stream interface for message producers and consumers.
/// </summary>
public interface IStreamTransport : IDisposable
{
    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    Stream Stream { get; }

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets information about the transport connection.
    /// </summary>
    string ConnectionInfo { get; }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Establishes the transport connection.
    /// </summary>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    Task ConnectAsync();

    /// <summary>
    /// Establishes the transport connection with retry support.
    /// </summary>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    Task ConnectAsync(ConnectionRetryOptions? retryOptions);

    /// <summary>
    /// Closes the transport connection.
    /// </summary>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    Task DisconnectAsync();

    /// <summary>
    /// Establishes the transport connection synchronously.
    /// </summary>
    void Connect();

    /// <summary>
    /// Closes the transport connection synchronously.
    /// </summary>
    void Disconnect();
}