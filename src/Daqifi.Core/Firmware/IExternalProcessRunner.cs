namespace Daqifi.Core.Firmware;

/// <summary>
/// Abstraction for launching external tools used by firmware update workflows.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>
    /// Executes an external process request.
    /// </summary>
    /// <param name="request">Process execution request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured execution result.</returns>
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default);
}
