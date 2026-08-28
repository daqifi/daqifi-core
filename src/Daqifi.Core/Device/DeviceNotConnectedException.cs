using System;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Thrown by a device operation's up-front connectivity guard when the device is not in a state
/// that can carry the request — it was never connected, it has already disconnected, or a
/// <see cref="DaqifiDevice.Disconnect"/> / <see cref="DaqifiDevice.Dispose()"/> is in flight on
/// another thread.
/// </summary>
/// <remarks>
/// <para>
/// The distinction this type exists to give callers is between an <em>ordinary, expected</em>
/// condition — the device went away, the user pressed Disconnect while a refresh was in flight,
/// WiFi dropped mid-transfer — and a genuine application defect. Clients that classify failures
/// for user-facing reporting or error tracking should log this at warning level with reconnect
/// guidance, and reserve error level (and alerting) for the exceptions that really do indicate
/// a bug. Before this type existed the only way to tell them apart was to match on the
/// exception message, which broke silently on any wording change.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> — the type these guards threw before —
/// so existing <c>catch (InvalidOperationException)</c> sites keep working unchanged, while new
/// code can catch this specific type.
/// </para>
/// <para>
/// <see cref="Communication.Transport.TransportNotConnectedException"/>, which reports that the
/// underlying transport's stream is gone, made the same trade one layer down. The two are
/// deliberately siblings rather than one deriving from the other: a device can fail this guard
/// while holding a perfectly healthy transport (for instance, mid-<see cref="DaqifiDevice.Disconnect"/>),
/// and a transport can drop while the device still reports <see cref="DaqifiDevice.IsConnected"/>.
/// </para>
/// </remarks>
public class DeviceNotConnectedException : InvalidOperationException
{
    /// <summary>
    /// The message used when no explicit message is supplied. Kept byte-for-byte identical to
    /// the message these guards threw before this type existed, so any downstream code still
    /// matching on it continues to work during migration.
    /// </summary>
    private const string DefaultMessage = "Device is not connected.";

    /// <summary>
    /// Gets a value indicating whether the guard fired because the device is tearing down —
    /// a <see cref="DaqifiDevice.Disconnect"/> or <see cref="DaqifiDevice.Dispose()"/> is in
    /// flight, or has already completed — rather than because the device was simply not
    /// connected in the first place.
    /// </summary>
    /// <remarks>
    /// Both cases are "the device is unavailable" and most callers can treat them the same way.
    /// The flag is here for callers that want to say something more specific, such as
    /// suppressing a retry prompt when the user themselves initiated the disconnect.
    /// </remarks>
    public bool IsShuttingDown { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceNotConnectedException"/> class with
    /// the default message.
    /// </summary>
    public DeviceNotConnectedException()
        : this(DefaultMessage, isShuttingDown: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceNotConnectedException"/> class with a
    /// specified error message.
    /// </summary>
    /// <param name="message">The message that describes the device's connectivity state.</param>
    public DeviceNotConnectedException(string message)
        : this(message, isShuttingDown: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceNotConnectedException"/> class with a
    /// specified error message and teardown state.
    /// </summary>
    /// <param name="message">The message that describes the device's connectivity state.</param>
    /// <param name="isShuttingDown">
    /// <c>true</c> when the guard fired because the device is disconnecting or disposing;
    /// otherwise <c>false</c>. See <see cref="IsShuttingDown"/>.
    /// </param>
    public DeviceNotConnectedException(string message, bool isShuttingDown)
        : base(message)
    {
        IsShuttingDown = isShuttingDown;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceNotConnectedException"/> class with a
    /// specified error message and a reference to the inner exception that is the cause of this
    /// exception.
    /// </summary>
    /// <param name="message">The message that describes the device's connectivity state.</param>
    /// <param name="innerException">The exception that caused the current exception, or <c>null</c>.</param>
    public DeviceNotConnectedException(string message, Exception? innerException)
        : this(message, innerException, isShuttingDown: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceNotConnectedException"/> class with a
    /// specified error message, the inner exception that caused it, and the teardown state.
    /// </summary>
    /// <remarks>
    /// Use this when a guard translates a lower-level teardown exception — such as the
    /// <see cref="ObjectDisposedException"/> from a <see cref="DaqifiDevice.Dispose()"/> racing
    /// ahead of an in-flight call — so the original type and stack survive on
    /// <see cref="Exception.InnerException"/> rather than being discarded.
    /// </remarks>
    /// <param name="message">The message that describes the device's connectivity state.</param>
    /// <param name="innerException">The exception that caused the current exception, or <c>null</c>.</param>
    /// <param name="isShuttingDown">
    /// <c>true</c> when the guard fired because the device is disconnecting or disposing;
    /// otherwise <c>false</c>. See <see cref="IsShuttingDown"/>.
    /// </param>
    public DeviceNotConnectedException(string message, Exception? innerException, bool isShuttingDown)
        : base(message, innerException)
    {
        IsShuttingDown = isShuttingDown;
    }
}
