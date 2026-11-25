namespace Daqifi.Core.Device;

/// <summary>
/// Where a device is in its life. The one answer <see cref="IDevice.State"/> gives, and the value
/// <see cref="IDevice.StateChanged"/> reports every change of.
/// </summary>
/// <remarks>
/// A session is up in five of these: <see cref="Connected"/>, <see cref="Initializing"/>,
/// <see cref="Ready"/>, <see cref="InitializationFailed"/> and <see cref="Disconnecting"/>. Code
/// that only wants to know whether there is a session asks <see cref="IDevice.IsConnected"/>, or
/// <see cref="DeviceStateExtensions.IsConnected"/> of a value it already holds, and does not
/// compare against <see cref="Connected"/>.
/// </remarks>
public enum DeviceState
{
    /// <summary>
    /// There is no session, and nothing is trying to build one: the device was never connected,
    /// was disconnected on request, or a connect failed.
    /// </summary>
    Disconnected,

    /// <summary>A connect is opening the transport.</summary>
    Connecting,

    /// <summary>
    /// The session is up and has not been initialized. A connect arrives here, and so does an
    /// initialization that was cancelled, which is not a device fault.
    /// </summary>
    Connected,

    /// <summary>The initialization sequence is running.</summary>
    Initializing,

    /// <summary>Initialization succeeded: the device is usable.</summary>
    Ready,

    /// <summary>
    /// Initialization failed. The session is still up, so the initialization can be retried.
    /// </summary>
    InitializationFailed,

    /// <summary>
    /// A live session is being torn down. <see cref="IDevice.IsConnected"/> is still <c>true</c>,
    /// because an exchange in flight gets a bounded courtesy wait to finish, but no new one may
    /// start, and a transport drop in this window is not a lost connection. A teardown of a device
    /// with no live session has nothing to protect and does not pass through here.
    /// </summary>
    Disconnecting,

    /// <summary>
    /// The connection dropped without anyone asking for it. A requested disconnect never reports
    /// this.
    /// </summary>
    // The message pumps stay bound to the dead stream until the next connect or disconnect, because
    // this is reached on the thread of the reader or the watchdog and cannot join them from there.
    Lost,

    /// <summary>
    /// Between automatic reconnect attempts after a drop (see <see cref="ReconnectOptions"/>). Only
    /// ever seen when reconnect is enabled; with the default policy a drop stops at
    /// <see cref="Lost"/>.
    /// </summary>
    Retrying,

    /// <summary>
    /// Automatic reconnection ran out of attempts and gave up (see
    /// <see cref="ReconnectOptions.MaxAttempts"/>). Nothing further is attempted without a new
    /// connect. Only ever seen when reconnect is enabled.
    /// </summary>
    Failed,

    /// <summary>The device has been disposed. Terminal.</summary>
    Disposed
}
