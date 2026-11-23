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
    /// Error message from the device.
    /// </summary>
    /// <remarks>
    /// Pinned to <c>4</c>, the value it had before the never-returned <c>SdCard</c> member was
    /// removed (issue #612), so a value persisted or compiled against 1.x keeps its meaning.
    /// </remarks>
    Error = 4
}
