using System.Diagnostics;

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
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInputResponseFactory != null,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start process '{request.FileName}'.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new SystemProcessHandle(process);
    }
}
