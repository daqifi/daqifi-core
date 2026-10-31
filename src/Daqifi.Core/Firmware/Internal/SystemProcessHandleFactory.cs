namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// Starts real operating-system processes for <see cref="ProcessExternalProcessRunner"/>.
/// </summary>
internal sealed class SystemProcessHandleFactory : IProcessHandleFactory
{
    internal static SystemProcessHandleFactory Instance { get; } = new();

    /// <inheritdoc />
    public IProcessHandle Start(ExternalProcessRequest request)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsJobProcessHandle.Start(request);
        }

        return UnixProcessGroupHandle.Start(request);
    }
}
