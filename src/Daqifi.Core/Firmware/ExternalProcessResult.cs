namespace Daqifi.Core.Firmware;

/// <summary>
/// Result payload for external process execution.
/// </summary>
public sealed class ExternalProcessResult
{
    /// <summary>
    /// Initializes a new process result.
    /// </summary>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="timedOut">Whether execution ended due to timeout.</param>
    /// <param name="duration">Observed execution duration.</param>
    /// <param name="standardOutputLines">Captured stdout lines.</param>
    /// <param name="standardErrorLines">Captured stderr lines.</param>
    public ExternalProcessResult(
        int exitCode,
        bool timedOut,
        TimeSpan duration,
        IReadOnlyList<string> standardOutputLines,
        IReadOnlyList<string> standardErrorLines)
    {
        ExitCode = exitCode;
        TimedOut = timedOut;
        Duration = duration;
        StandardOutputLines = standardOutputLines ?? Array.Empty<string>();
        StandardErrorLines = standardErrorLines ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the process exit code.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets a value indicating whether process execution timed out.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>
    /// Gets the process execution duration.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets captured stdout lines.
    /// </summary>
    public IReadOnlyList<string> StandardOutputLines { get; }

    /// <summary>
    /// Gets captured stderr lines.
    /// </summary>
    public IReadOnlyList<string> StandardErrorLines { get; }
}
