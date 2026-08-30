using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The slice of <see cref="DaqifiDevice"/> that <see cref="ReconnectSupervisor"/> needs: the
/// lifecycle primitives it rebuilds a session with, the session snapshot seam it restores
/// through, and the three events plus the device error surface it reports on.
/// </summary>
/// <remarks>
/// <para>
/// Every member forwards to something the device already had. The device implements this
/// explicitly, so none of it widens the public API — the same arrangement
/// <see cref="ITextExchangeHost"/>, <see cref="IDeviceOperationHost"/> and
/// <see cref="IOperationSerializationHost"/> use.
/// </para>
/// <para>
/// The events are raised through methods here rather than by the supervisor holding the
/// delegates itself, because the <c>sender</c> a subscriber sees has to stay the device that
/// declares the event. The device's own safe-raise helper is what these forward to, so a
/// subscriber that throws still cannot take the loop down.
/// </para>
/// </remarks>
internal interface IReconnectHost
{
    /// <summary>
    /// The device's logger. The supervisor wraps every call to it, so a throwing logger cannot
    /// take down the reconnect loop.
    /// </summary>
    ILogger Logger { get; }

    /// <summary>The device's name, as it appears in this group's log lines and events.</summary>
    string Name { get; }

    /// <summary>
    /// The clock the backoff ladder and the reported outage duration are measured on (issue
    /// #637). <see cref="TimeProvider.System"/> on a real device; a test substitutes a fake one
    /// and walks a 91-second ladder in milliseconds.
    /// </summary>
    TimeProvider TimeProvider { get; }

    /// <summary>The device's current connection status.</summary>
    ConnectionStatus Status { get; }

    /// <summary>Whether the device has been disposed.</summary>
    bool IsDisposed { get; }

    /// <summary>Whether a teardown is currently running on the device.</summary>
    bool IsDisconnecting { get; }

    /// <summary>Whether the device has a transport at all. A device built over a bare stream has none, and nothing to reconnect.</summary>
    bool HasTransport { get; }

    /// <summary>
    /// Which way the caller last pointed the device. The session epoch says <i>that</i> a caller
    /// superseded the loop; this says <i>what they wanted</i>, which is what decides how the loop
    /// unwinds a session it has already brought up.
    /// </summary>
    bool CallerWantsDisconnected { get; }

    /// <summary>
    /// Opens the transport and starts the pumps, without the supersede step a caller's
    /// <c>Connect()</c> performs — the loop must not cancel itself.
    /// </summary>
    void ConnectCore();

    /// <summary>
    /// Tears the session down and settles the device on <paramref name="finalStatus"/>.
    /// </summary>
    void DisconnectCore(ConnectionStatus finalStatus);

    /// <summary>
    /// Re-runs the device's initialization handshake on a freshly reopened transport. Virtual on
    /// the device, so a derived device's override is what runs.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records the session state a reconnect should restore. Virtual on the device and a no-op
    /// on the base class; <c>DaqifiStreamingDevice</c> records the enabled channels, the
    /// streaming frequency, and whether a stream was running.
    /// </summary>
    void CaptureSessionSnapshot();

    /// <summary>
    /// Re-applies the state recorded by <see cref="CaptureSessionSnapshot"/> to a device that has
    /// just been reconnected and re-initialized.
    /// </summary>
    /// <returns><c>true</c> if an interrupted stream was restarted.</returns>
    Task<bool> RestoreSessionSnapshotAsync(ReconnectOptions options, CancellationToken cancellationToken);

    /// <summary>Raises <c>ReconnectAttempt</c> on the device, isolated from a throwing subscriber.</summary>
    void RaiseReconnectAttempt(ReconnectAttemptEventArgs args);

    /// <summary>Raises <c>Reconnected</c> on the device, isolated from a throwing subscriber.</summary>
    void RaiseReconnected(ReconnectedEventArgs args);

    /// <summary>Raises <c>ReconnectFailed</c> on the device, isolated from a throwing subscriber.</summary>
    void RaiseReconnectFailed(ReconnectFailedEventArgs args);

    /// <summary>
    /// Reports a failure on the device's general <c>ErrorOccurred</c> surface, so that giving up
    /// is impossible to miss even with nothing subscribed to <c>ReconnectFailed</c>.
    /// </summary>
    void RaiseDeviceError(DeviceErrorSource source, Exception error);
}
