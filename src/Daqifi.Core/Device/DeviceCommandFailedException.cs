namespace Daqifi.Core.Device;

/// <summary>
/// Thrown by the confirming (<c>...Async</c>) device commands when the device did not accept the
/// command — or when it never said whether it did.
/// </summary>
/// <remarks>
/// <para>
/// A command that is sent and never asked about is indistinguishable from one the device refused.
/// Bench evidence on a real Nyquist running firmware 3.7.2: <c>CONFigure:ADC:LOADcal</c> answers
/// <c>-200,"Execution error"</c> on a unit whose user calibration bank was never written. The
/// confirming commands ask: the calibration ones read the device's SCPI error queue, and
/// <see cref="IStreamingDevice.StartStreamingAsync"/>,
/// <see cref="IStreamingDevice.StopStreamingAsync"/> and
/// <see cref="IStreamingDevice.RebootAsync"/> each state their own confirmation. All raise this
/// exception rather than report a success the device never gave.
/// </para>
/// <para>
/// <see cref="ErrorCode"/> and <see cref="DaqifiDeviceException.RawDeviceResponse"/> together say how
/// much is known:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Code present</b> — the device answered and the answer was a refusal. The command did not
/// take effect, and retrying it unchanged will be refused again.
/// </description></item>
/// <item><description>
/// <b>No code, but a response line</b> — the device answered, but with nothing carrying a readable
/// code: a bare <c>ERROR</c>, a queue reply whose code would not parse, or a state that contradicts
/// the command. The command is not confirmed, and the raw line is all there is to go on.
/// </description></item>
/// <item><description>
/// <b>Neither</b> (no code and an empty response) — nothing readable came back at all, so
/// whether the command was applied is <em>unknown</em>. Usually the link or the device needs
/// attention before a retry is meaningful. A command that was never sent has neither too, and its
/// message says so.
/// </description></item>
/// </list>
/// <para>
/// None of the three is a success, which is why all of them throw.
/// </para>
/// <para>
/// <see cref="DaqifiDeviceException.RawDeviceResponse"/> holds at most one line here: the one the
/// verdict was read from.
/// </para>
/// </remarks>
public class DeviceCommandFailedException : DaqifiDeviceException
{
    /// <summary>
    /// The SCPI command whose outcome this exception reports, e.g. <c>CONFigure:ADC:LOADcal</c>.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// The SCPI error code the device reported, e.g. <c>-200</c> for an execution error; <c>null</c>
    /// when no code could be read — either because nothing came back or because what did carried no
    /// readable code. Never <c>0</c>: that is the value SCPI reserves for "no error", so a refusal
    /// is never reported under it. Pair with <see cref="DaqifiDeviceException.RawDeviceResponse"/>
    /// to tell an unreadable refusal from silence.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance reporting that the device <em>refused</em> the command.
    /// </summary>
    /// <remarks>
    /// Passing <c>0</c> is not a refusal — SCPI reserves it for "no error" — so it is recorded as
    /// <see cref="ErrorCode"/> <c>null</c> ("no readable code") rather than stored as-is, and the
    /// message says so. That keeps the never-<c>0</c> contract true no matter who calls this,
    /// which is what lets consumers branch on <see cref="ErrorCode"/> at all.
    /// <para>
    /// Deliberately normalised rather than rejected with an <see cref="ArgumentOutOfRangeException"/>.
    /// Exceptions are constructed on failure paths, and a constructor that throws would replace a
    /// diagnosable device failure with an argument error — losing the diagnosis, which is the very
    /// failure mode this type exists to prevent. The device's line is preserved either way, so
    /// nothing the device said is lost.
    /// </para>
    /// </remarks>
    /// <param name="command">The SCPI command that was sent.</param>
    /// <param name="errorCode">The SCPI error code the device reported. <c>0</c> is normalised to <c>null</c> — see the remarks.</param>
    /// <param name="deviceResponse">The raw device line the code was read from.</param>
    public DeviceCommandFailedException(string command, int errorCode, string deviceResponse)
        : base(BuildRefusalMessage(command, errorCode, deviceResponse), ToLines(deviceResponse))
    {
        Command = command;
        ErrorCode = errorCode == 0 ? null : errorCode;
    }

    private static string BuildRefusalMessage(string command, int errorCode, string deviceResponse)
        => errorCode == 0
            // Not the "unreadable code" wording: the code here is perfectly readable, it just
            // says "no error", which cannot describe a refusal. Saying it is unreadable would be
            // factually wrong about a line the reader can see for themselves.
            ? $"The device did not confirm '{command}': it answered {deviceResponse}, which reports "
              + "SCPI code 0 (\"no error\") and so does not describe a refusal. No usable verdict "
              + "was returned, so whether the command was applied is unknown."
            : $"The device rejected '{command}' with SCPI error {errorCode} ({deviceResponse}). "
              + "The command did not take effect.";

    /// <summary>
    /// Initializes a new instance reporting that no SCPI error code could be read — either the
    /// device said nothing readable (outcome unknown), or it complained in a form carrying no code
    /// (a refusal whose reason is only in the raw line).
    /// </summary>
    /// <param name="command">The SCPI command that was sent.</param>
    /// <param name="deviceResponse">The raw device line, when there was one; <c>null</c> when nothing readable came back.</param>
    public DeviceCommandFailedException(string command, string? deviceResponse = null)
        : base(BuildUnreadableVerdictMessage(command, deviceResponse), ToLines(deviceResponse))
    {
        Command = command;
        ErrorCode = null;
    }

    /// <summary>
    /// Initializes a new instance with a message of its own, for a command whose confirmation is
    /// something other than the error queue.
    /// </summary>
    private DeviceCommandFailedException(
        string command, string? deviceResponse, string message, Exception? innerException = null)
        : base(message, ToLines(deviceResponse), innerException)
    {
        Command = command;
        ErrorCode = null;
    }

    /// <summary>
    /// The device gave no usable answer, so whether the command was applied is unknown.
    /// </summary>
    internal static DeviceCommandFailedException Unconfirmed(string command, string? deviceResponse)
        => new(
            command,
            deviceResponse,
            $"The device did not confirm '{command}'"
            + (deviceResponse == null ? string.Empty : $"; it answered {deviceResponse}")
            + ". The command was sent, so whether it was applied is unknown.");

    /// <summary>
    /// The device answered, and its answer says the command did not take effect.
    /// </summary>
    /// <param name="command">The SCPI command that was sent.</param>
    /// <param name="what">What the device said, as a verb phrase following "The device".</param>
    /// <param name="deviceResponse">The raw line it said that in.</param>
    internal static DeviceCommandFailedException Contradicted(string command, string what, string deviceResponse)
        => new(
            command,
            deviceResponse,
            $"The device {what} after '{command}' (it answered {deviceResponse}). "
            + "The command did not take effect.");

    /// <summary>
    /// The command never reached the device.
    /// </summary>
    /// <param name="command">The SCPI command.</param>
    /// <param name="reason">Why it was not sent.</param>
    internal static DeviceCommandFailedException NotSent(string command, Exception reason)
        => new(command, null, $"'{command}' was not sent to the device. {reason.Message}", reason);

    private static string[]? ToLines(string? deviceResponse)
        => deviceResponse == null ? null : [deviceResponse];

    private static string BuildUnreadableVerdictMessage(string command, string? deviceResponse)
        => deviceResponse == null
            ? $"The device did not confirm '{command}': its error queue was not readable after the "
              + "command, so whether it was applied is unknown. Check the connection and retry."
            : $"The device did not confirm '{command}': it answered {deviceResponse}, which carries no "
              + "readable SCPI error code. The command was not confirmed to have taken effect.";
}
