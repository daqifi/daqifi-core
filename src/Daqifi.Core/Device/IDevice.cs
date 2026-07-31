using Daqifi.Core.Communication.Messages;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Base interface for all DAQiFi devices.
    /// </summary>
    public interface IDevice
    {
        /// <summary>
        /// Gets the name of the device.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the IP address of the device.
        /// </summary>
        IPAddress? IpAddress { get; }

        /// <summary>
        /// Gets a value indicating whether the device is connected.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the current connection status of the device.
        /// </summary>
        ConnectionStatus Status { get; }

        /// <summary>
        /// Occurs when the device status changes.
        /// </summary>
        event EventHandler<DeviceStatusEventArgs> StatusChanged;

        /// <summary>
        /// Occurs when a message is received from the device.
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

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
        /// The default implementation simply calls <see cref="Connect"/> on the calling thread, so
        /// an existing <see cref="IDevice"/> implementation keeps compiling and working unchanged —
        /// it just cannot honor the token beyond the check made before the attempt starts.
        /// <see cref="DaqifiDevice"/> overrides it with a genuinely asynchronous, cancellable
        /// implementation.
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
        /// <returns>A task representing the asynchronous connect operation.</returns>
        /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
        Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disconnects from the device without blocking the calling thread.
        /// </summary>
        /// <remarks>
        /// The default implementation simply calls <see cref="Disconnect"/> on the calling thread,
        /// so an existing <see cref="IDevice"/> implementation keeps compiling and working
        /// unchanged. <see cref="DaqifiDevice"/> overrides it with a genuinely asynchronous
        /// implementation; see that override for what the token does — teardown always runs to
        /// completion, so cancellation shortens the wait rather than aborting the disconnect.
        /// </remarks>
        /// <param name="cancellationToken">
        /// A cancellation token to observe while disconnecting. Ignored by this default
        /// implementation: aborting a teardown part-way would leave the device in an
        /// indeterminate state, which is worse than finishing it.
        /// </param>
        /// <returns>A task representing the asynchronous disconnect operation.</returns>
        Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Disconnect();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a message to the device.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send<T>(IOutboundMessage<T> message);
    }
}
