using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Every state change a <see cref="DaqifiDevice"/> may make.
/// </summary>
internal static class DeviceLifecycleTable
{
    /// <summary>
    /// The permitted changes. <c>DaqifiDevice.TryTransition</c> is the only writer of the
    /// device's state and it refuses anything not listed here.
    /// </summary>
    /// <remarks>
    /// A refusal is routine rather than a defect, which is why this differs from
    /// <c>FirmwareUpdateContext.AllowedTransitions</c> in not throwing: several threads legitimately
    /// race to report on one session. An initialization finishing after a disconnect is the plain
    /// example. The disconnect won, and the late report must not bring back a state that is no
    /// longer true.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<DeviceState, IReadOnlySet<DeviceState>> AllowedTransitions
        = new Dictionary<DeviceState, IReadOnlySet<DeviceState>>
        {
            // The last three are the settled states of a reconnect attempt that failed to open
            // the transport: FailConnect has already moved it here.
            [DeviceState.Disconnected] = new HashSet<DeviceState>
            {
                DeviceState.Connecting,
                DeviceState.Disposed,
                DeviceState.Retrying,
                DeviceState.Lost,
                DeviceState.Failed
            },
            // The last three are a teardown that gave up waiting on a wedged connect, and settled
            // the device without it. See DaqifiDevice.MarkDisconnectedWithoutTeardown.
            [DeviceState.Connecting] = new HashSet<DeviceState>
            {
                DeviceState.Connected,
                DeviceState.Disconnected,
                DeviceState.Disposed,
                DeviceState.Retrying,
                DeviceState.Lost,
                DeviceState.Failed
            },
            [DeviceState.Connected] = new HashSet<DeviceState>
            {
                DeviceState.Initializing,
                DeviceState.Connecting,
                DeviceState.Disconnecting,
                DeviceState.Lost,
                DeviceState.Disposed
            },
            // Connected is a cancelled initialization, which is not a device fault.
            [DeviceState.Initializing] = new HashSet<DeviceState>
            {
                DeviceState.Ready,
                DeviceState.Connected,
                DeviceState.InitializationFailed,
                DeviceState.Connecting,
                DeviceState.Disconnecting,
                DeviceState.Lost,
                DeviceState.Disposed
            },
            [DeviceState.Ready] = new HashSet<DeviceState>
            {
                DeviceState.Connecting,
                DeviceState.Disconnecting,
                DeviceState.Lost,
                DeviceState.Disposed
            },
            [DeviceState.InitializationFailed] = new HashSet<DeviceState>
            {
                DeviceState.Initializing,
                DeviceState.Connecting,
                DeviceState.Disconnecting,
                DeviceState.Lost,
                DeviceState.Disposed
            },
            [DeviceState.Disconnecting] = new HashSet<DeviceState>
            {
                DeviceState.Disconnected,
                DeviceState.Retrying,
                DeviceState.Lost,
                DeviceState.Failed,
                DeviceState.Disposed
            },
            [DeviceState.Lost] = new HashSet<DeviceState>
            {
                DeviceState.Connecting,
                DeviceState.Disconnected,
                DeviceState.Retrying,
                DeviceState.Failed,
                DeviceState.Disposed
            },
            [DeviceState.Retrying] = new HashSet<DeviceState>
            {
                DeviceState.Connecting,
                DeviceState.Disconnected,
                DeviceState.Lost,
                DeviceState.Failed,
                DeviceState.Disposed
            },
            [DeviceState.Failed] = new HashSet<DeviceState>
            {
                DeviceState.Connecting,
                DeviceState.Disconnected,
                DeviceState.Disposed
            },
            [DeviceState.Disposed] = new HashSet<DeviceState>()
        };

    /// <summary>Whether the table permits moving from <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal static bool IsAllowed(DeviceState from, DeviceState to) =>
        AllowedTransitions[from].Contains(to);

    /// <summary>
    /// Whether a session is up and nobody has started taking it down: the values a transport drop
    /// counts as a lost connection on, and the ones a teardown has to mark before it starts. Narrower
    /// than <see cref="DeviceStateExtensions.IsConnected"/> by <see cref="DeviceState.Disconnecting"/>.
    /// </summary>
    internal static bool IsLive(DeviceState state) => state
        is DeviceState.Connected
        or DeviceState.Initializing
        or DeviceState.Ready
        or DeviceState.InitializationFailed;
}
