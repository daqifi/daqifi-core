using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The one value that says where a <see cref="DaqifiDevice"/> is in its life (issue #931).
/// <see cref="DaqifiDevice.Status"/> and <see cref="DaqifiDevice.State"/> are both calculated from
/// it, so they cannot disagree.
/// </summary>
internal enum DeviceLifecycle
{
    /// <summary>No session, and nothing is trying to build one.</summary>
    Disconnected,

    /// <summary>A connect is opening the transport.</summary>
    Connecting,

    /// <summary>The session is up and has not been initialized.</summary>
    Connected,

    /// <summary>The initialization sequence is running.</summary>
    Initializing,

    /// <summary>Initialization succeeded.</summary>
    Ready,

    /// <summary>
    /// Initialization failed. The session is still up, so a caller can retry it.
    /// </summary>
    Error,

    /// <summary>
    /// A live session is being torn down. It still reports itself connected, because an in-flight
    /// exchange gets a bounded courtesy wait to finish, but no new exchange may start and a
    /// transport drop in this window is not a lost connection. A teardown of a device with no live
    /// session has nothing to protect and stays on the value it had until it settles.
    /// </summary>
    Disconnecting,

    /// <summary>
    /// The connection dropped without anyone asking. The message pumps stay bound to the dead
    /// stream until the next connect or disconnect, because this is reached on the thread of the
    /// reader or the watchdog and cannot join them from there.
    /// </summary>
    Lost,

    /// <summary>Between automatic reconnect attempts.</summary>
    Retrying,

    /// <summary>Automatic reconnection ran out of attempts.</summary>
    Failed,

    /// <summary>The device has released its resources. Terminal.</summary>
    Disposed
}

/// <summary>
/// Every lifecycle change a <see cref="DaqifiDevice"/> may make, and how each value reads through
/// the public <see cref="ConnectionStatus"/> and <see cref="DeviceState"/>.
/// </summary>
internal static class DeviceLifecycleTable
{
    /// <summary>
    /// The permitted changes. <c>DaqifiDevice.TryTransition</c> is the only writer of the lifecycle
    /// value and it refuses anything not listed here.
    /// </summary>
    /// <remarks>
    /// A refusal is routine rather than a defect, which is why this differs from
    /// <c>FirmwareUpdateContext.AllowedTransitions</c> in not throwing: several threads legitimately
    /// race to report on one session. An initialization finishing after a disconnect is the plain
    /// example. The disconnect won, and the late report must not bring back a state that is no
    /// longer true.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<DeviceLifecycle, IReadOnlySet<DeviceLifecycle>> AllowedTransitions
        = new Dictionary<DeviceLifecycle, IReadOnlySet<DeviceLifecycle>>
        {
            // The last three are the settled states of a reconnect attempt that failed to open
            // the transport: FailConnect has already moved it here.
            [DeviceLifecycle.Disconnected] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disposed,
                DeviceLifecycle.Retrying,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Failed
            },
            // The last three are a teardown that gave up waiting on a wedged connect, and settled
            // the device without it. See DaqifiDevice.MarkDisconnectedWithoutTeardown.
            [DeviceLifecycle.Connecting] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connected,
                DeviceLifecycle.Disconnected,
                DeviceLifecycle.Disposed,
                DeviceLifecycle.Retrying,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Failed
            },
            [DeviceLifecycle.Connected] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Initializing,
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnecting,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Disposed
            },
            // Connected is a cancelled initialization, which is not a device fault.
            [DeviceLifecycle.Initializing] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Ready,
                DeviceLifecycle.Connected,
                DeviceLifecycle.Error,
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnecting,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Ready] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnecting,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Error] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Initializing,
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnecting,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Disconnecting] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Disconnected,
                DeviceLifecycle.Retrying,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Failed,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Lost] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnected,
                DeviceLifecycle.Retrying,
                DeviceLifecycle.Failed,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Retrying] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnected,
                DeviceLifecycle.Lost,
                DeviceLifecycle.Failed,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Failed] = new HashSet<DeviceLifecycle>
            {
                DeviceLifecycle.Connecting,
                DeviceLifecycle.Disconnected,
                DeviceLifecycle.Disposed
            },
            [DeviceLifecycle.Disposed] = new HashSet<DeviceLifecycle>()
        };

    /// <summary>Whether the table permits moving from <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal static bool IsAllowed(DeviceLifecycle from, DeviceLifecycle to) =>
        AllowedTransitions[from].Contains(to);

    /// <summary>
    /// Whether a session is up and nobody has started taking it down: the values a transport drop
    /// counts as a lost connection on, and the ones a teardown has to mark before it starts.
    /// </summary>
    internal static bool IsLive(DeviceLifecycle lifecycle) => lifecycle
        is DeviceLifecycle.Connected
        or DeviceLifecycle.Initializing
        or DeviceLifecycle.Ready
        or DeviceLifecycle.Error;

    /// <summary>How a lifecycle value reads through <see cref="DaqifiDevice.Status"/>.</summary>
    internal static ConnectionStatus ToStatus(DeviceLifecycle lifecycle) => lifecycle switch
    {
        DeviceLifecycle.Disconnected => ConnectionStatus.Disconnected,
        DeviceLifecycle.Connecting => ConnectionStatus.Connecting,
        DeviceLifecycle.Connected => ConnectionStatus.Connected,
        DeviceLifecycle.Initializing => ConnectionStatus.Connected,
        DeviceLifecycle.Ready => ConnectionStatus.Connected,
        DeviceLifecycle.Error => ConnectionStatus.Connected,
        DeviceLifecycle.Disconnecting => ConnectionStatus.Connected,
        DeviceLifecycle.Lost => ConnectionStatus.Lost,
        DeviceLifecycle.Retrying => ConnectionStatus.Retrying,
        DeviceLifecycle.Failed => ConnectionStatus.Failed,
        DeviceLifecycle.Disposed => ConnectionStatus.Disconnected,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null)
    };

    /// <summary>
    /// How a lifecycle value reads through <see cref="DaqifiDevice.State"/>. Lost, Retrying and
    /// Failed all read <see cref="DeviceState.Disconnected"/>: there is no session in any of them,
    /// and <see cref="DaqifiDevice.Status"/> is what says why. A device being torn down has already
    /// stopped being ready, so it reads <see cref="DeviceState.Connected"/>.
    /// </summary>
    internal static DeviceState ToState(DeviceLifecycle lifecycle) => lifecycle switch
    {
        DeviceLifecycle.Disconnected => DeviceState.Disconnected,
        DeviceLifecycle.Connecting => DeviceState.Connecting,
        DeviceLifecycle.Connected => DeviceState.Connected,
        DeviceLifecycle.Initializing => DeviceState.Initializing,
        DeviceLifecycle.Ready => DeviceState.Ready,
        DeviceLifecycle.Error => DeviceState.Error,
        DeviceLifecycle.Disconnecting => DeviceState.Connected,
        DeviceLifecycle.Lost => DeviceState.Disconnected,
        DeviceLifecycle.Retrying => DeviceState.Disconnected,
        DeviceLifecycle.Failed => DeviceState.Disconnected,
        DeviceLifecycle.Disposed => DeviceState.Disconnected,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null)
    };

    /// <summary>
    /// The lifecycle value a teardown settles on, from the status its caller asked for. The reconnect
    /// loop talks in <see cref="ConnectionStatus"/>, and these four are the only ones it or the
    /// disconnect paths ask for.
    /// </summary>
    internal static DeviceLifecycle FromSettledStatus(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Lost => DeviceLifecycle.Lost,
        ConnectionStatus.Retrying => DeviceLifecycle.Retrying,
        ConnectionStatus.Failed => DeviceLifecycle.Failed,
        _ => DeviceLifecycle.Disconnected
    };
}
