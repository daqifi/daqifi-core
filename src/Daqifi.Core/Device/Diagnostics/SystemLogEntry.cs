namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// A single entry from the device system log, returned by the <c>SYSTem:LOG?</c> SCPI query.
/// </summary>
/// <remarks>
/// The firmware stores log entries as free-form text and does not currently prefix them with a
/// structured level, module, or timestamp, so only the raw <see cref="Message"/> is exposed.
/// Additional parsed fields may be added in a future firmware/library revision without breaking
/// this type (it uses init-only properties rather than positional record parameters).
/// </remarks>
public sealed record SystemLogEntry
{
    /// <summary>
    /// Gets the log message text (trimmed of trailing line endings).
    /// </summary>
    public required string Message { get; init; }
}
