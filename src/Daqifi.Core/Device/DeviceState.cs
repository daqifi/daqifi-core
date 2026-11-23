namespace Daqifi.Core.Device;

/// <summary>
/// Represents the operational state of a DAQiFi device.
/// </summary>
public enum DeviceState
{
    /// <summary>
    /// Device has no session: it was never connected, was disconnected, or lost its connection.
    /// <see cref="DaqifiDevice.Status"/> says which, including
    /// <see cref="ConnectionStatus.Lost"/>, <see cref="ConnectionStatus.Retrying"/> and
    /// <see cref="ConnectionStatus.Failed"/>.
    /// </summary>
    Disconnected,

    /// <summary>
    /// Device is in the process of connecting.
    /// </summary>
    Connecting,

    /// <summary>
    /// Device is connected but not yet initialized.
    /// </summary>
    Connected,

    /// <summary>
    /// Device is being initialized (running initialization sequence).
    /// </summary>
    Initializing,

    /// <summary>
    /// Device is fully initialized and ready for operations.
    /// </summary>
    Ready,

    /// <summary>
    /// Device is connected and its initialization failed. The initialization can be retried.
    /// </summary>
    /// <remarks>
    /// Pinned to <c>6</c>, its value since 1.x: the slot before it belonged to a <c>Streaming</c>
    /// member that nothing ever assigned and that was removed in 2.0. Live acquisition state is
    /// <see cref="IStreamingDevice.IsStreaming"/>.
    /// </remarks>
    Error = 6
}
