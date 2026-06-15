namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Thrown when a transport operation requires an active connection but the underlying
/// transport is not connected — for example, accessing <see cref="IStreamTransport.Stream"/>
/// after the serial port was closed by a device unplug or a DTR-triggered MCU reset that
/// re-enumerated the COM port mid-connect, or when a TCP connection was never established or
/// has since dropped.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch
/// <see cref="InvalidOperationException"/> around stream access continue to work unchanged.
/// New consumers can catch this specific type to classify a dropped/closed transport as a
/// transient, environmental condition (an unplug or reset re-enumeration) rather than an
/// application bug.
/// </para>
/// <para>
/// This is the stream-access analog of surfacing a typed <see cref="TimeoutException"/> for a
/// TCP connect timeout: the goal is to replace a raw framework message — such as
/// <c>"The BaseStream is only available when the port is open."</c> from
/// <see cref="System.IO.Ports.SerialPort.BaseStream"/> — with a clear, classifiable signal
/// that names the transport state.
/// </para>
/// </remarks>
public class TransportNotConnectedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransportNotConnectedException"/> class
    /// with a default message.
    /// </summary>
    public TransportNotConnectedException()
        : base("Transport is not connected.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportNotConnectedException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the transport state.</param>
    public TransportNotConnectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportNotConnectedException"/> class
    /// with a specified error message and a reference to the inner exception that is the cause
    /// of this exception.
    /// </summary>
    /// <param name="message">The message that describes the transport state.</param>
    /// <param name="innerException">The exception that caused the current exception, or <c>null</c>.</param>
    public TransportNotConnectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
