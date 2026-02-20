namespace Daqifi.Core.Firmware;

/// <summary>
/// Event data describing a firmware update state transition.
/// </summary>
public sealed class FirmwareUpdateStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FirmwareUpdateStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previousState">The previous state.</param>
    /// <param name="currentState">The new state.</param>
    /// <param name="operation">A short operation description associated with the transition.</param>
    public FirmwareUpdateStateChangedEventArgs(
        FirmwareUpdateState previousState,
        FirmwareUpdateState currentState,
        string operation)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Operation = operation ?? string.Empty;
        ChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets the previous state.
    /// </summary>
    public FirmwareUpdateState PreviousState { get; }

    /// <summary>
    /// Gets the new state.
    /// </summary>
    public FirmwareUpdateState CurrentState { get; }

    /// <summary>
    /// Gets the operation description associated with this transition.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Gets the UTC timestamp when this transition was recorded.
    /// </summary>
    public DateTimeOffset ChangedAtUtc { get; }
}
