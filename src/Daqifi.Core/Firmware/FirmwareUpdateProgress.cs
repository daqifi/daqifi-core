namespace Daqifi.Core.Firmware;

/// <summary>
/// Progress payload emitted by firmware update operations.
/// </summary>
public sealed class FirmwareUpdateProgress
{
    /// <summary>
    /// Gets or sets the active firmware update state.
    /// </summary>
    public FirmwareUpdateState State { get; set; }

    /// <summary>
    /// Gets or sets the completion percentage in the range 0-100.
    /// </summary>
    public double PercentComplete { get; set; }

    /// <summary>
    /// Gets or sets a human-readable description of the current operation.
    /// </summary>
    public string CurrentOperation { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of bytes written so far.
    /// </summary>
    public long BytesWritten { get; set; }

    /// <summary>
    /// Gets or sets the total number of bytes expected for the operation.
    /// </summary>
    public long TotalBytes { get; set; }
}
