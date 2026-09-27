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
    /// Reserved. <see cref="ProtobufProtocolHandler.DetectMessageType"/> never returns
    /// this value: SD card responses arrive on the text/raw-byte path, not as live
    /// protobuf frames. Kept so existing callers can still name the slot.
    /// </summary>
    SdCard,

    /// <summary>
    /// Error message from the device.
    /// </summary>
    Error
}
