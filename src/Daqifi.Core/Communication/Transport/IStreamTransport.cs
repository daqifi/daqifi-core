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
    /// <para>
    /// This is the cancellable form of <see cref="ConnectAsync(ConnectionRetryOptions?)"/>. It has a
    /// default implementation, so an existing <see cref="IStreamTransport"/> implementation keeps
    /// compiling and working unchanged. That default honors the token only <i>before</i> the attempt
    /// starts and then forwards to the uncancellable overload, which cannot be interrupted once it
    /// is running.
    /// </para>
    /// <para>
    /// The pre-check is not a formality: opening a connection the caller has already given up on has
    /// real side effects — a serial open pulses DTR and resets the MCU — so refusing to start is the
    /// one part of this contract every implementation can keep. Implementations that can also
    /// abandon an attempt already in flight should override this member; the transports shipped in
    /// daqifi-core do.
    /// </para>
    /// </remarks>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
    Task ConnectAsync(ConnectionRetryOptions? retryOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectAsync(retryOptions);
    }

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