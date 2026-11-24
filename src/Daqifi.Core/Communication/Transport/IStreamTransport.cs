namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Represents a transport mechanism that provides stream-based communication.
/// Abstracts the underlying connection type (TCP or serial) and provides
/// a unified Stream interface for message producers and consumers. HID
/// (<see cref="IHidTransport"/>) is a separate, non-stream transport. UDP has no
/// transport abstraction: device discovery uses <see cref="System.Net.Sockets.UdpClient"/>
/// directly.
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
    /// Gets a value indicating whether this transport reaches the device over USB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device needs to know its link type: the SD card and the WiFi/LAN module share one SPI
    /// bus, so every SD operation prepares that bus differently on USB than on the network (see
    /// <see cref="Daqifi.Core.Device.DaqifiStreamingDevice.IsUsbConnection"/>, which reads this).
    /// The transport is the one thing that knows what it is, so the answer lives here rather than
    /// in a type test on the device (issue #612).
    /// </para>
    /// <para>
    /// Every implementation must answer, because a wrong answer is silent: a USB transport that
    /// reports <c>false</c> makes the device refuse SD logging and skip the USB stream-interface
    /// setup. <see cref="SerialStreamTransport"/> returns <c>true</c>;
    /// <see cref="TcpStreamTransport"/> returns <c>false</c>. A custom transport over any USB link
    /// (a CDC serial port reached through a platform USB API, for example) returns <c>true</c>.
    /// The answer is a fact about the kind of link, not its state, so it does not change with
    /// <see cref="IsConnected"/>.
    /// </para>
    /// </remarks>
    bool IsUsbConnection { get; }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    /// <remarks>
    /// Handlers run synchronously on whatever thread raised the change, and no particular thread
    /// is guaranteed. A drop is raised from the watchdog or reader thread that detected it. A
    /// connect is raised from a thread-pool thread when the dial actually suspends, or inline on
    /// the calling thread when it does not. Treat the thread as arbitrary: a UI consumer must
    /// marshal to its own dispatcher before touching controls, and should post rather than block
    /// on it, because the UI thread may itself be blocked in
    /// <see cref="Daqifi.Core.Device.DaqifiDevice.Connect"/>.
    /// </remarks>
    event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Establishes the transport connection, abandoning the attempt if
    /// <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancellation covers the whole call: before an attempt starts, while one is in flight, and
    /// while waiting out the backoff delay between retries. Starting a connection the caller has
    /// already given up on has real side effects (a serial open pulses DTR and resets the MCU), so
    /// an implementation must not begin an attempt once the token is signalled.
    /// </para>
    /// <para>
    /// A canceled attempt leaves the transport disconnected and reusable: a later call may connect
    /// it. An implementation over a native open that cannot be interrupted must still return
    /// promptly, and must release the handle itself when that open finally completes.
    /// </para>
    /// <para>
    /// <see cref="Daqifi.Core.Device.DaqifiDevice.Connect"/> blocks on this method, so an
    /// implementation must not need the thread it was called on in order to finish.
    /// </para>
    /// </remarks>
    /// <param name="retryOptions">Configuration for retry behavior. If null, makes a single attempt.</param>
    /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
    Task ConnectAsync(
        ConnectionRetryOptions? retryOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the transport connection.
    /// </summary>
    /// <remarks>
    /// Cancellation never leaves the connection open and never throws
    /// <see cref="OperationCanceledException"/>: the token only cuts short anything an
    /// implementation waits for before closing (a graceful shutdown handshake, say). This matches
    /// <see cref="Daqifi.Core.Device.DaqifiDevice.DisconnectAsync"/>. The transports shipped in
    /// daqifi-core wait for nothing, so the token has no effect on them.
    /// </remarks>
    /// <param name="cancellationToken">Cuts short any wait that precedes the close.</param>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
