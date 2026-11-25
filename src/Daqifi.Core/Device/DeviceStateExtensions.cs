namespace Daqifi.Core.Device;

/// <summary>
/// Questions about a <see cref="DeviceState"/> value.
/// </summary>
public static class DeviceStateExtensions
{
    /// <summary>
    /// Whether a device in this state has a session up, which is what
    /// <see cref="IDevice.IsConnected"/> reports.
    /// </summary>
    /// <remarks>
    /// For a <see cref="IDevice.StateChanged"/> handler: the device may have moved on by the time
    /// the handler runs, so <see cref="IDevice.IsConnected"/> answers for now and this answers for
    /// the change being reported.
    /// </remarks>
    /// <param name="state">The state to ask about.</param>
    /// <returns><c>true</c> if a session is up in this state.</returns>
    public static bool IsConnected(this DeviceState state) => state
        is DeviceState.Connected
        or DeviceState.Initializing
        or DeviceState.Ready
        or DeviceState.InitializationFailed
        or DeviceState.Disconnecting;
}
