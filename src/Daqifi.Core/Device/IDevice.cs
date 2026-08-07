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
        /// Sends a message to the device.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send<T>(IOutboundMessage<T> message);
    }
}
