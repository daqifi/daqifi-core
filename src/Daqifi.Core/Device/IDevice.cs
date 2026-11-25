using Daqifi.Core.Communication.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device;

/// <summary>
/// Base interface for all DAQiFi devices.
/// </summary>
/// <remarks>
/// Extends <see cref="IAsyncDisposable"/> so a consumer holding only this interface — the point
/// of coding against it in the first place — can tear a device down without a cast to
/// <see cref="DaqifiDevice"/>. Implement it the way <see cref="DaqifiDevice.DisposeAsync"/> does:
/// disconnect, then release resources, guarded so it is safe to call more than once.
/// </remarks>
public interface IDevice : IAsyncDisposable
{
    /// <summary>
    /// Gets the name of the device.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the device has a session up:
    /// <see cref="DeviceStateExtensions.IsConnected"/> of <see cref="State"/>.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets where the device is in its life.
    /// </summary>
    DeviceState State { get; }

    /// <summary>
    /// Gets a value indicating whether this device supports <paramref name="feature"/>. Branch on
    /// this rather than comparing firmware versions or board types (ADR 0001).
    /// </summary>
    /// <remarks>
    /// Answered from what the device has reported so far, on every call. A firmware requirement
    /// fails closed: an unknown or unreadable version is not support. A board or hardware
    /// requirement is not applied until the device has reported its board.
    /// </remarks>
    /// <param name="feature">The feature to test.</param>
    /// <returns><c>true</c> if the device meets every requirement for <paramref name="feature"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feature"/> is not a defined feature.</exception>
    bool Supports(DeviceFeature feature);

    /// <summary>
    /// Occurs on every change of <see cref="State"/>, in the order the changes were made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handlers run synchronously on whatever thread raised the change, and no particular
    /// thread is guaranteed: a drop is raised from the watchdog or reader thread that detected
    /// it, a reconnect from the reconnect supervisor, and a connect either from a thread-pool
    /// thread or inline on the calling thread depending on whether the dial suspended. The
    /// library resumes its awaits off the caller's <see cref="SynchronizationContext"/> so the
    /// synchronous <see cref="Connect"/> facade cannot deadlock a UI thread (issue #495), but
    /// that only declines the context — it is not a promise of a thread hop. Treat the thread
    /// as arbitrary: a UI consumer must marshal to its own dispatcher before touching controls.
    /// </para>
    /// <para>
    /// Events are raised one at a time. A change made while a handler runs,
    /// by that handler or by another thread, is raised after the handler returns: a handler must
    /// not wait for a later event, and <see cref="State"/> may already be further on than
    /// <see cref="DeviceStateChangedEventArgs.CurrentState"/>.
    /// </para>
    /// <para>
    /// <see cref="DeviceState.Connecting"/> and <see cref="DeviceState.Disconnecting"/> are raised by
    /// a connect or disconnect that is still running and waits for the handler, so a handler of these
    /// two must not block on another one made from another thread. For every other event it may.
    /// </para>
    /// </remarks>
    event EventHandler<DeviceStateChangedEventArgs> StateChanged;

    /// <summary>
    /// Occurs when a message is received from the device.
    /// </summary>
    event EventHandler<MessageReceivedEventArgs> MessageReceived;

    /// <summary>
    /// Occurs when something fails on one of the device's background threads — a read from the
    /// transport stream, a parse, a dispatch to a subscriber, or the decode of a streaming frame.
    /// </summary>
    /// <remarks>
    /// Observational only: it reports what went wrong and changes nothing about what the device
    /// does (issue #378). Raises are throttled per source and exception type — see
    /// <see cref="DaqifiDevice.ErrorOccurred"/> for the policy and the guarantees.
    /// </remarks>
    event EventHandler<DeviceErrorEventArgs> ErrorOccurred;

    /// <summary>
    /// Connects to the device, blocking the calling thread until the connection is open.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ConnectAsync"/> on a UI thread, or whenever the attempt needs to be
    /// abandonable.
    /// </remarks>
    void Connect();

    /// <summary>
    /// Disconnects from the device, blocking the calling thread until teardown completes.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="DisconnectAsync"/> on a UI thread — teardown waits for any in-flight
    /// command exchange to finish, which can take seconds.
    /// </remarks>
    void Disconnect();

    /// <summary>
    /// Connects to the device, abandoning the attempt if <paramref name="cancellationToken"/>
    /// is signalled.
    /// </summary>
    /// <remarks>
    /// A genuine member, not a default-interface-method shim over <see cref="Connect"/> — every
    /// implementer must honor the token, not merely check it before the attempt starts. See
    /// <see cref="DaqifiDevice.ConnectAsync"/> for the reference implementation.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the device without blocking the calling thread.
    /// </summary>
    /// <remarks>
    /// A genuine member, not a default-interface-method shim over <see cref="Disconnect"/>. See
    /// <see cref="DaqifiDevice.DisconnectAsync"/> for the reference implementation and for what
    /// the token does there — teardown always runs to completion, so cancellation shortens the
    /// wait rather than aborting the disconnect; other implementers may choose differently.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while disconnecting.</param>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a connected device into a known state and waits until it has described itself, moving
    /// <see cref="State"/> to <see cref="DeviceState.Ready"/>. Does nothing when the device is
    /// already initialized.
    /// </summary>
    /// <remarks>
    /// The devices <see cref="DaqifiDeviceFactory"/> connects are already initialized unless
    /// <see cref="DeviceConnectionOptions.InitializeDevice"/> is off. Call this after connecting a
    /// device yourself. By default it stops a stream any session has running on the device; see
    /// <see cref="DaqifiDevice.InitializeAsync"/>.
    /// </remarks>
    /// <param name="channelPopulationTimeout">
    /// How long to wait for the device to describe itself. Defaults to 8 seconds when omitted.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to observe while initializing.</param>
    /// <returns>A task that completes once the device is ready.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelPopulationTimeout"/> is not positive.</exception>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="ScpiInitializationErrorException">Thrown when the device reports a SCPI error that persists after a retry.</exception>
    /// <exception cref="TimeoutException">Thrown when the device does not describe itself in time.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the device.
    /// </summary>
    /// <param name="message">The message to send.</param>
    void Send<T>(IOutboundMessage<T> message);
}
