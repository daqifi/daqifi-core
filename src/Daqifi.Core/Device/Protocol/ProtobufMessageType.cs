namespace Daqifi.Core.Device.Protocol;

/// <summary>
/// Represents the type of protobuf message received from a DAQiFi device.
/// </summary>
public enum ProtobufMessageType
{
    /// <summary>
    /// Unknown or unrecognized message type.
    /// </summary>
    Unknown,

    /// <summary>
    /// Status message containing device information and configuration.
    /// </summary>
    Status,

    /// <summary>
    /// Streaming data message containing sensor readings.
    /// </summary>
    Stream,

    /// <summary>
    /// SD card related message (file listings, etc.).
    /// </summary>
    SdCard,

    /// <summary>
    /// Error message from the device.
    /// </summary>
    Error
}
