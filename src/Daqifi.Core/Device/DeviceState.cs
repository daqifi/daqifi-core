namespace Daqifi.Core.Device;

/// <summary>
/// Represents the operational state of a DAQiFi device.
/// </summary>
public enum DeviceState
{
    /// <summary>
    /// Device is not connected or has been disconnected.
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
    /// Named for a device that is actively acquiring samples, but never assigned.
    /// After initialization, <see cref="DaqifiDevice.State"/> stays <see cref="Ready"/>;
    /// live acquisition state is <see cref="IStreamingDevice.IsStreaming"/>.
    /// </summary>
    Streaming,

    /// <summary>
    /// Device has encountered an error.
    /// </summary>
    Error
}
