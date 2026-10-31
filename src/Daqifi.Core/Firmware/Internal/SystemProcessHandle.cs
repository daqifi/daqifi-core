using System.Diagnostics;

namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// <see cref="IProcessHandle"/> over a real <see cref="Process"/>.
/// </summary>
internal sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _process;

    internal SystemProcessHandle(Process process)
    {
        _process = process;
    }

    /// <inheritdoc />
    public bool HasExited => _process.HasExited;

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <inheritdoc />
    public TextReader StandardOutput => _process.StandardOutput;

    /// <inheritdoc />
    public TextReader StandardError => _process.StandardError;

    /// <inheritdoc />
    public TextWriter StandardInput => _process.StandardInput;

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <inheritdoc />
    public void KillProcessTree()
    {
        // No HasExited test first, on purpose. The launcher exiting is exactly the case this
        // kill has to cover: the WiFi flash tool is a .cmd that starts a worker and returns,
        // so the launcher is often gone while the worker still holds the redirected pipes
        // (issue #845). A test for HasExited turns that case into "kill nothing".
        //
        // Process.Kill(entireProcessTree: true) exists for both target frameworks (net9.0 and
        // net10.0) and on every platform the library supports; it is not Windows-only. It does
        // throw when the tree is already gone, so ProcessExternalProcessRunner treats a failure
        // here as non-fatal.
        _process.Kill(entireProcessTree: true);
    }

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
