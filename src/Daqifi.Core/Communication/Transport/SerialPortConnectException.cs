namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Thrown when <see cref="SerialStreamTransport.ConnectAsync(ConnectionRetryOptions?, CancellationToken)"/>
/// cannot open its serial port, carrying a stable <see cref="Reason"/> that says why.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> <see cref="System.IO.Ports.SerialPort.Open"/> reports a missing port
/// on macOS and Linux as <c>UnauthorizedAccessException: Access to the port '...' is denied.</c>,
/// which sends users looking for a permissions problem when the real cause is a wrong or stale
/// port name — the common case, because USB serial device nodes are renumbered across replugs.
/// This is the serial analog of the <see cref="TimeoutException"/> that
/// <see cref="TcpStreamTransport"/> substitutes for a misleading <c>TaskCanceledException</c>
/// (daqifi-desktop#517): the same failure, named accurately.
/// </para>
/// <para>
/// <b>Why a typed reason.</b> The platform exception cannot be classified after the fact. Measured
/// on macOS with System.IO.Ports 10.0.10, a missing port and a port held by another process
/// produce byte-identical exceptions — same type, same message, and an inner
/// <see cref="IOException"/> with the same text and the same (bogus, non-errno)
/// <see cref="Exception.HResult"/>. Matching on message text or on the inner exception is
/// therefore not merely unportable, it does not work at all. <see cref="Reason"/> is derived from
/// evidence gathered around the failure instead — see
/// <see cref="SerialStreamTransport.ConnectAsync(ConnectionRetryOptions?, CancellationToken)"/>.
/// </para>
/// <para>
/// <b>Base type.</b> Derives from <see cref="IOException"/>: failing to open an I/O device is an
/// I/O error, and on Windows a non-existent port already surfaces as an
/// <see cref="IOException"/>-derived exception, so a caller that brackets a connect with
/// <c>catch (IOException)</c> keeps working. The original platform exception is always preserved
/// as <see cref="Exception.InnerException"/>.
/// </para>
/// <para>
/// <b>Retries.</b> This type does not change retry behavior — a failed open is still one failed
/// attempt under <see cref="ConnectionRetryOptions"/>. It does let a caller decide whether
/// retrying is worthwhile: <see cref="SerialPortConnectFailure.InUse"/> can clear on its own,
/// while <see cref="SerialPortConnectFailure.NotFound"/> and
/// <see cref="SerialPortConnectFailure.AccessDenied"/> will not.
/// </para>
/// </remarks>
public class SerialPortConnectException : IOException
{
    /// <summary>
    /// Gets the name of the port that could not be opened (for example <c>COM3</c> or
    /// <c>/dev/cu.usbmodem1101</c>).
    /// </summary>
    public string PortName { get; }

    /// <summary>
    /// Gets the classified reason the port could not be opened.
    /// </summary>
    public SerialPortConnectFailure Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerialPortConnectException"/> class.
    /// </summary>
    /// <param name="portName">The port that could not be opened.</param>
    /// <param name="reason">The classified reason for the failure.</param>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">
    /// The platform exception that caused the failure, or <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="portName"/> is null.</exception>
    public SerialPortConnectException(
        string portName,
        SerialPortConnectFailure reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        PortName = portName ?? throw new ArgumentNullException(nameof(portName));
        Reason = reason;
    }

    /// <summary>
    /// Builds the translated exception for a failed <see cref="System.IO.Ports.SerialPort.Open"/>,
    /// preserving <paramref name="error"/> as the inner exception.
    /// </summary>
    /// <param name="portName">The port that could not be opened.</param>
    /// <param name="error">The platform exception the open threw.</param>
    /// <param name="portPresent">
    /// Whether the port is still visible to the system, or <c>null</c> if that could not be
    /// observed. See <see cref="Classify"/>.
    /// </param>
    /// <param name="permissionGateRuledOut">
    /// Whether a permission failure has been positively ruled out, or <c>null</c> if unknown.
    /// See <see cref="Classify"/>.
    /// </param>
    internal static SerialPortConnectException FromOpenFailure(
        string portName,
        Exception error,
        bool? portPresent,
        bool? permissionGateRuledOut)
    {
        var reason = Classify(error, portPresent, permissionGateRuledOut);
        return new SerialPortConnectException(portName, reason, DescribeFailure(portName, reason), error);
    }

    /// <summary>
    /// Classifies a failed serial open from evidence collected around it, rather than from the
    /// platform exception's type or text.
    /// </summary>
    /// <param name="error">The platform exception the open threw.</param>
    /// <param name="portPresent">
    /// <c>true</c> if the port is still enumerated (or its device node still exists),
    /// <c>false</c> if it is definitely absent, <c>null</c> if the probe could not answer. A
    /// failure to observe is never treated as absence.
    /// </param>
    /// <param name="permissionGateRuledOut">
    /// <c>true</c> when the platform cannot be denying access on permission grounds — a Unix
    /// device node that grants read and write to every user, or a Windows COM port, which has no
    /// equivalent per-user gate. <c>false</c> when a permission failure is possible, <c>null</c>
    /// when it could not be determined.
    /// </param>
    /// <returns>The classified reason.</returns>
    /// <remarks>
    /// <para>
    /// The order matters. An explicit <see cref="FileNotFoundException"/> — what Windows reports
    /// for a port that no longer exists — is conclusive on its own. Otherwise absence of the port
    /// is the deciding signal, and it is the one that fixes the reported bug: it is portable, it
    /// does not read any exception text, and it is the same probe the transport already trusts to
    /// detect an unplug on an established connection.
    /// </para>
    /// <para>
    /// Only once the port is known (or assumed) to exist does an access-denied exception have to be
    /// split between "someone else has it" and "you may not have it", and that split is the one
    /// piece the platform genuinely cannot answer on macOS. It is resolved from
    /// <paramref name="permissionGateRuledOut"/>, and when that is unavailable the result stays
    /// <see cref="SerialPortConnectFailure.AccessDenied"/> — the status-quo wording — so an
    /// unfamiliar platform degrades to today's behavior instead of asserting something false.
    /// </para>
    /// </remarks>
    internal static SerialPortConnectFailure Classify(
        Exception error,
        bool? portPresent,
        bool? permissionGateRuledOut)
    {
        ArgumentNullException.ThrowIfNull(error);

        // Windows names the missing-port case outright. Checked before anything else, and through
        // the inner exception too, because it needs no corroboration.
        if (error is FileNotFoundException || error.InnerException is FileNotFoundException)
        {
            return SerialPortConnectFailure.NotFound;
        }

        // The port is definitely gone. This is the case the issue is about, and the only one where
        // "access is denied" was actively misleading.
        if (portPresent == false)
        {
            return SerialPortConnectFailure.NotFound;
        }

        if (error is UnauthorizedAccessException || error.InnerException is UnauthorizedAccessException)
        {
            return permissionGateRuledOut == true
                ? SerialPortConnectFailure.InUse
                : SerialPortConnectFailure.AccessDenied;
        }

        return SerialPortConnectFailure.Unknown;
    }

    /// <summary>
    /// Produces the user-facing message for a classified failure.
    /// </summary>
    /// <param name="portName">The port that could not be opened.</param>
    /// <param name="reason">The classified reason.</param>
    /// <returns>A single-sentence description naming the port.</returns>
    internal static string DescribeFailure(string portName, SerialPortConnectFailure reason) => reason switch
    {
        SerialPortConnectFailure.NotFound => $"Serial port '{portName}' was not found.",
        SerialPortConnectFailure.InUse => $"Serial port '{portName}' is in use.",
        // Deliberately the framework's own wording for the one case where it was always accurate,
        // so anything already keying on that phrasing for a real permission problem still matches.
        SerialPortConnectFailure.AccessDenied => $"Access to the port '{portName}' is denied.",
        _ => $"Serial port '{portName}' could not be opened."
    };
}
