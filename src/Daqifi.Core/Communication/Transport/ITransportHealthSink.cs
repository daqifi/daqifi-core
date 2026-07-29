namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Optional capability implemented by a transport that wants to be told how the I/O on its
/// <see cref="IStreamTransport.Stream"/> is actually going, so it can notice a connection that
/// died underneath it.
/// </summary>
/// <remarks>
/// <para>
/// A transport only ever sees its own handle, and an OS handle is a poor liveness signal:
/// <see cref="System.IO.Ports.SerialPort.IsOpen"/> stays <c>true</c> after a USB device is
/// physically unplugged, and <see cref="System.Net.Sockets.TcpClient.Connected"/> reflects the
/// last completed operation rather than the current link. The component that actually drives the
/// stream — a <see cref="Consumers.StreamMessageConsumer{T}"/> reader loop or a
/// <see cref="Producers.MessageProducer{T}"/> writer loop — is the first to know, and this
/// interface is the path back from it to the transport (issue #382).
/// </para>
/// <para>
/// Implementations must be safe to call from any thread and must tolerate being called at a high
/// rate: <see cref="ReportIoSuccess"/> runs once per successful read.
/// </para>
/// <para>
/// A single failure means nothing — serial and socket reads fail transiently. Implementations are
/// expected to escalate only on a run of consecutive failures with no successful transfer in
/// between, so a recoverable blip never tears down a healthy connection.
/// </para>
/// <para>
/// Callers must not report an operation that merely hit its configured timeout: that is what an
/// idle device (or one that is momentarily not draining its receive buffer) looks like, and
/// treating it as a failure would disconnect a healthy connection.
/// </para>
/// </remarks>
public interface ITransportHealthSink
{
    /// <summary>
    /// Reports that a read or write against the transport's stream failed for a reason other than
    /// an expected idle timeout.
    /// </summary>
    /// <param name="error">The exception the failed operation raised.</param>
    void ReportIoFault(Exception error);

    /// <summary>
    /// Reports that a read or write against the transport's stream completed successfully, which
    /// clears any run of failures accumulated so far.
    /// </summary>
    void ReportIoSuccess();
}
