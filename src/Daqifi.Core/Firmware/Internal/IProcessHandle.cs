namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// The part of <see cref="System.Diagnostics.Process"/> that
/// <see cref="ProcessExternalProcessRunner"/> needs, behind an interface so the runner's
/// timeout, kill and output-drain ordering can be tested without starting a real process -
/// including the cases that only occur on Windows (issue #845).
/// </summary>
internal interface IProcessHandle : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the process this handle was created for has exited.
    /// A descendant it started may still be running.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Gets the exit code. Throws when the process has not exited, exactly as
    /// <see cref="System.Diagnostics.Process.ExitCode"/> does.
    /// </summary>
    int ExitCode { get; }

    /// <summary>
    /// Gets the reader for the process's redirected standard output.
    /// </summary>
    TextReader StandardOutput { get; }

    /// <summary>
    /// Gets the reader for the process's redirected standard error.
    /// </summary>
    TextReader StandardError { get; }

    /// <summary>
    /// Gets the writer for the process's redirected standard input.
    /// </summary>
    TextWriter StandardInput { get; }

    /// <summary>
    /// Waits for the process to exit.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kills the process and every descendant it still has. Callers must treat a failure as
    /// non-fatal: the implementation throws when the process, or a member of its tree, is
    /// already gone.
    /// </summary>
    void KillProcessTree();
}
