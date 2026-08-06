using System;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Thrown by the confirming (<c>...Async</c>) device-administration commands when the device did
    /// not accept the command — or when it never said whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fire-and-forget administration commands (<see cref="IStreamingDevice.LoadAdcCalibration"/>
    /// and friends) return <c>void</c> and parse no reply, so a device that refuses the command is
    /// indistinguishable from one that carried it out. Bench evidence on a real Nyquist running
    /// firmware 3.7.2: <c>CONFigure:ADC:LOADcal</c> answers <c>-200,"Execution error"</c> on a unit
    /// whose user calibration bank was never written, and the caller learns nothing. The confirming
    /// variants close that gap by reading the device's SCPI error queue after the command and
    /// raising this exception rather than reporting a success the device never gave.
    /// </para>
    /// <para>
    /// <see cref="ErrorCode"/> separates the two conditions this covers. A non-null code is the
    /// device's own verdict: it answered, and the answer was a refusal — the command did not take
    /// effect. A <c>null</c> code means the outcome is <em>unknown</em>: the verification query went
    /// unanswered, so the command may or may not have been applied. Neither is a success, which is
    /// why both surface as an exception, but a caller that retries should treat them differently —
    /// a refusal will be refused again, whereas an unanswered query usually means the link or the
    /// device needs attention first.
    /// </para>
    /// </remarks>
    public class DeviceCommandFailedException : Exception
    {
        /// <summary>
        /// The SCPI command whose outcome this exception reports, e.g. <c>CONFigure:ADC:LOADcal</c>.
        /// </summary>
        public string Command { get; }

        /// <summary>
        /// The SCPI error code the device reported, e.g. <c>-200</c> for an execution error; <c>null</c>
        /// when the device never answered the verification query and the command's outcome is therefore
        /// unknown rather than known-bad.
        /// </summary>
        public int? ErrorCode { get; }

        /// <summary>
        /// The raw device line this verdict was read from, when there was one.
        /// </summary>
        public string? DeviceResponse { get; }

        /// <summary>
        /// Initializes a new instance reporting that the device <em>refused</em> the command.
        /// </summary>
        /// <param name="command">The SCPI command that was sent.</param>
        /// <param name="errorCode">The SCPI error code the device reported.</param>
        /// <param name="deviceResponse">The raw device line the code was read from.</param>
        public DeviceCommandFailedException(string command, int errorCode, string deviceResponse)
            : base($"The device rejected '{command}' with SCPI error {errorCode} ({deviceResponse}). "
                   + "The command did not take effect.")
        {
            Command = command;
            ErrorCode = errorCode;
            DeviceResponse = deviceResponse;
        }

        /// <summary>
        /// Initializes a new instance reporting that the command's outcome could <em>not be
        /// confirmed</em> — the device did not answer the verification query.
        /// </summary>
        /// <param name="command">The SCPI command that was sent.</param>
        /// <param name="deviceResponse">The raw device line that was expected to carry the verdict, if any.</param>
        public DeviceCommandFailedException(string command, string? deviceResponse = null)
            : base($"The device did not confirm '{command}': its error queue was not readable after the "
                   + "command, so whether it was applied is unknown. Check the connection and retry.")
        {
            Command = command;
            ErrorCode = null;
            DeviceResponse = deviceResponse;
        }
    }
}
