#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Why an SD card file transfer stalled before the <c>__END_OF_FILE__</c> marker arrived.
/// Carried by <see cref="SdCardTransferStalledException"/> so callers can tell an ordinary
/// transport stall (retryable, expected on a busy device) from a transport that is gone.
/// </summary>
public enum SdCardTransferStallReason
{
    /// <summary>
    /// The transport returned zero bytes while still reporting itself readable. Over USB
    /// serial — the transport SD download runs on in practice — this is the <em>ordinary</em>
    /// stall signal, not an end-of-stream: Core sets a per-read <c>SerialPort.ReadTimeout</c>
    /// and .NET's <c>SerialStream.ReadAsync</c> returns 0 on a read timeout rather than
    /// throwing or honoring the cancellation token. On a stream-oriented network transport a
    /// zero-length read can instead mean the peer closed the connection, which the stream
    /// itself gives no way to tell apart; either way the download did not complete and a
    /// retry is the reasonable response.
    /// </summary>
    NoDataReceived = 0,

    /// <summary>
    /// The transport stream is no longer readable — it was closed or disposed underneath the
    /// transfer. Unlike <see cref="NoDataReceived"/>, retrying the download on the same
    /// transport cannot succeed.
    /// </summary>
    TransportClosed = 1,

    /// <summary>
    /// The overall transfer deadline elapsed before the EOF marker arrived.
    /// </summary>
    TransferTimeout = 2
}
