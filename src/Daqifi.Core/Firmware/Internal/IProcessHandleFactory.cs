namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// Starts the process an <see cref="ExternalProcessRequest"/> describes.
/// </summary>
internal interface IProcessHandleFactory
{
    /// <summary>
    /// Starts the process and returns a handle to it.
    /// </summary>
    /// <param name="request">The request describing the process to start.</param>
    /// <returns>A handle to the started process.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    IProcessHandle Start(ExternalProcessRequest request);
}
