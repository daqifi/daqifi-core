namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Why a serial port could not be opened, as reported by
/// <see cref="SerialPortConnectException.Reason"/>.
/// </summary>
/// <remarks>
/// This is the stable, typed alternative to inspecting the platform exception a failed
/// <see cref="System.IO.Ports.SerialPort.Open"/> produces. That exception carries no reliable
/// classification of its own: on macOS a missing port and a busy port both surface as
/// <see cref="UnauthorizedAccessException"/> ("Access to the port '...' is denied.") wrapping an
/// <see cref="IOException"/> whose message and <see cref="Exception.HResult"/> are the same in
/// both cases, so neither the type, the text, nor the errno can tell them apart.
/// </remarks>
public enum SerialPortConnectFailure
{
    /// <summary>
    /// The reason could not be determined. The platform exception is still available as
    /// <see cref="Exception.InnerException"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The port does not exist. Usually a typo or a stale port name — USB serial device nodes are
    /// renumbered across replugs, so a name captured earlier in a session may no longer resolve.
    /// Retrying does not help; re-enumerate the available ports instead.
    /// </summary>
    NotFound,

    /// <summary>
    /// The port exists but another process already holds it open. Retrying can succeed once the
    /// other process releases the port.
    /// </summary>
    InUse,

    /// <summary>
    /// The port exists but the current process is not permitted to open it — for example a Linux
    /// device node owned by a group the user is not a member of (commonly <c>dialout</c>).
    /// Retrying does not help until the permission is granted.
    /// </summary>
    AccessDenied
}
