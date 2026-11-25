namespace Daqifi.Core.Firmware;

/// <summary>
/// Progress payload emitted by firmware update operations.
/// </summary>
public sealed class FirmwareUpdateProgress
{
    /// <summary>
    /// Gets the active firmware update state.
    /// </summary>
    public FirmwareUpdateState State { get; init; }

    /// <summary>
    /// Gets the completion percentage in the range 0-100.
    /// </summary>
    public double PercentComplete { get; init; }

    /// <summary>
    /// Gets a human-readable description of the current operation.
    /// </summary>
    public string CurrentOperation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of bytes written so far.
    /// </summary>
    public long BytesWritten { get; init; }

    /// <summary>
    /// Gets the total number of bytes expected for the operation.
    /// </summary>
    public long TotalBytes { get; init; }
}
