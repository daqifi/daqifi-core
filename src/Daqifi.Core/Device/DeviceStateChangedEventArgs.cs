using System;

namespace Daqifi.Core.Device;

/// <summary>
/// Provides data for the <see cref="IDevice.StateChanged"/> event.
/// </summary>
public sealed class DeviceStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previousState">The state the device left.</param>
    /// <param name="currentState">The state the device moved to.</param>
    public DeviceStateChangedEventArgs(DeviceState previousState, DeviceState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    /// <summary>
    /// Gets the state the device left.
    /// </summary>
    public DeviceState PreviousState { get; }

    /// <summary>
    /// Gets the state the device moved to. <see cref="IDevice.State"/> may already be further on.
    /// </summary>
    public DeviceState CurrentState { get; }
}
