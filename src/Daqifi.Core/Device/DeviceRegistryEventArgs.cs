namespace Daqifi.Core.Device;

/// <summary>
/// Why a device left a <see cref="DaqifiDeviceRegistry"/>.
/// </summary>
public enum DeviceRemovalReason
{
    /// <summary>
    /// The caller removed it explicitly.
    /// </summary>
    Removed,

    /// <summary>
    /// It was dropped in favor of a duplicate connection to the same physical unit, because the
    /// duplicate policy returned <see cref="DuplicateDeviceAction.SwitchToNew"/>.
    /// </summary>
    Replaced,

    /// <summary>
    /// The registry found the device no longer reporting <see cref="DaqifiDevice.IsConnected"/>
    /// (unplugged, rebooted, or dropped by the transport) and pruned the stale registration.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The whole live set was cleared by <see cref="DaqifiDeviceRegistry.Clear"/>.
    /// </summary>
    Cleared
}

/// <summary>
/// Event data for <see cref="DaqifiDeviceRegistry.DeviceAdded"/>.
/// </summary>
public sealed class DeviceRegisteredEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceRegisteredEventArgs"/> class.
    /// </summary>
    /// <param name="registration">The registration that was added.</param>
    public DeviceRegisteredEventArgs(DeviceRegistration registration)
    {
        Registration = registration;
    }

    /// <summary>
    /// Gets the registration that was added to the live set.
    /// </summary>
    public DeviceRegistration Registration { get; }
}

/// <summary>
/// Event data for <see cref="DaqifiDeviceRegistry.DeviceRemoved"/>.
/// </summary>
/// <remarks>
/// The registry disconnects and disposes the device before raising this event, so subscribers must
/// treat <see cref="DeviceRegistration.Device"/> as a dead handle — read its
/// <see cref="DaqifiDevice.Name"/> or <see cref="DeviceRegistration.Identity"/> for a message, but
/// do not send to it.
/// </remarks>
public sealed class DeviceRemovedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceRemovedEventArgs"/> class.
    /// </summary>
    /// <param name="registration">The registration that was removed.</param>
    /// <param name="reason">Why it was removed.</param>
    public DeviceRemovedEventArgs(DeviceRegistration registration, DeviceRemovalReason reason)
    {
        Registration = registration;
        Reason = reason;
    }

    /// <summary>
    /// Gets the registration that was removed from the live set.
    /// </summary>
    public DeviceRegistration Registration { get; }

    /// <summary>
    /// Gets why the device was removed.
    /// </summary>
    public DeviceRemovalReason Reason { get; }
}
