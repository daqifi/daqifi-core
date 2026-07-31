namespace Daqifi.Core.Device;

/// <summary>
/// Identifies which part of a device's background pipeline produced a
/// <see cref="DeviceErrorEventArgs"/>.
/// </summary>
/// <remarks>
/// The source is the first thing a diagnostic needs: "no samples" caused by a stream that cannot be
/// read is a different problem from a stream that reads fine but cannot be decoded, and before
/// issue #378 both looked identical from outside the library (silence).
/// </remarks>
public enum DeviceErrorSource
{
    /// <summary>
    /// The source could not be classified.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The background message consumer: a failed read from the transport stream, a parse failure,
    /// or an exception thrown while dispatching a parsed message to subscribers.
    /// </summary>
    /// <remarks>
    /// A persistent run of read failures is <em>also</em> what the transport escalates into
    /// <see cref="ConnectionStatus.Lost"/> (issue #377). Seeing this source is therefore not by
    /// itself proof the link is gone — watch <see cref="DaqifiDevice.StatusChanged"/> for that.
    /// </remarks>
    MessageConsumer = 1,

    /// <summary>
    /// Per-frame decoding of a streaming data frame into channel samples
    /// (<see cref="DaqifiStreamingDevice"/>). The frame is dropped; the stream keeps running.
    /// </summary>
    StreamDecode = 2,
}
