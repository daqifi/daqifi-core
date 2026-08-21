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

    /// <summary>
    /// Automatic reconnection after a dropped connection (issue #379): every allowed attempt
    /// failed, so the session is gone for good.
    /// </summary>
    /// <remarks>
    /// Unlike the other sources this one is terminal rather than incidental — it is raised exactly
    /// once, when the device gives up, and it is the loud counterpart to
    /// <see cref="DaqifiDevice.ReconnectFailed"/>. A reconnect that is cancelled does not raise it:
    /// stopping on request is not a failure.
    /// </remarks>
    Reconnect = 3,

    /// <summary>
    /// A <see cref="DaqifiDevice.StatusChanged"/> subscriber threw while being notified of a
    /// connection-status transition (issue #494).
    /// </summary>
    /// <remarks>
    /// The transition itself already happened and is not rolled back; this reports only that one
    /// consumer callback failed. A cross-thread UI exception is the common cause, since the event
    /// is raised on whichever background thread observed the change. The library's own reaction to a
    /// transition is isolated the same way and reported here too, so a defect inside Core cannot
    /// break a transition either.
    /// </remarks>
    StatusNotification = 4,
}
