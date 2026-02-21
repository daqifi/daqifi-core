namespace Daqifi.Core.Firmware;

/// <summary>
/// Represents a firmware update failure with the state/operation context.
/// </summary>
public class FirmwareUpdateException : Exception
{
    /// <summary>
    /// Initializes a new firmware update exception.
    /// </summary>
    /// <param name="failedState">The state where the failure occurred.</param>
    /// <param name="operation">The operation that was attempted.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="recoveryGuidance">Optional user-facing recovery guidance.</param>
    /// <param name="innerException">Optional inner exception.</param>
    public FirmwareUpdateException(
        FirmwareUpdateState failedState,
        string operation,
        string message,
        string? recoveryGuidance = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailedState = failedState;
        Operation = operation ?? string.Empty;
        RecoveryGuidance = recoveryGuidance;
    }

    /// <summary>
    /// Gets the state where the failure occurred.
    /// </summary>
    public FirmwareUpdateState FailedState { get; }

    /// <summary>
    /// Gets the operation that was attempted when the failure occurred.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Gets optional guidance that can help recover from this failure.
    /// </summary>
    public string? RecoveryGuidance { get; }
}
