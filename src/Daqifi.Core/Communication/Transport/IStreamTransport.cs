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
    /// <exception cref="TransportNotConnectedException">
    /// Thrown when the transport is not connected (e.g. never connected, or the underlying
    /// connection was closed or dropped).
    /// </exception>
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
    /// Establishes the transport connection, abandoning the attempt if
    /// <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
    Task ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(null, cancellationToken);

    /// <summary>
    /// Establishes the transport connection with retry support, abandoning the attempt if
    /// <paramref name="cancellationToken"/> is signalled — including while waiting out the
    /// backoff delay between retries.
    /// </summary>
    /// <remarks>
    /// This is the cancellable form of <see cref="ConnectAsync(ConnectionRetryOptions?)"/>. It has a
    /// default implementation that simply forwards to the uncancellable overload, so an existing
    /// <see cref="IStreamTransport"/> implementation keeps compiling and working unchanged — it just
    /// cannot honor the token. Implementations that can abandon an in-flight attempt should override
    /// this member; the transports shipped in daqifi-core do.
    /// </remarks>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
    Task ConnectAsync(ConnectionRetryOptions? retryOptions, CancellationToken cancellationToken) =>
        ConnectAsync(retryOptions);

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