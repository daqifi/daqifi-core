using System.Diagnostics.CodeAnalysis;

namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// Finds Microchip's external WINC flash tool under a firmware path, so a caller can answer
/// "can this machine flash the WiFi module?" before starting an update rather than discovering the
/// answer partway through.
/// </summary>
/// <remarks>
/// This is the check that separates Windows from Linux/macOS: Microchip ships the flash tool as a
/// Windows <c>.cmd</c>/<c>.exe</c>, so on other platforms it is genuinely absent rather than
/// misconfigured. It locates the tool; it does not run it — the WiFi update flow owns that,
/// including the interactive prompt handshake and output-based success verification.
/// </remarks>
public sealed class WincFlashToolLocator
{
    private readonly string _toolFileName;

    /// <summary>
    /// Creates a locator that searches for <paramref name="toolFileName"/> (e.g.
    /// <c>winc_flash_tool.cmd</c>).
    /// </summary>
    public WincFlashToolLocator(string toolFileName)
    {
        if (string.IsNullOrWhiteSpace(toolFileName))
        {
            throw new ArgumentException("Tool file name cannot be empty.", nameof(toolFileName));
        }

        _toolFileName = toolFileName;
    }

    /// <summary>
    /// Whether the flash tool is present for the given firmware path.
    /// </summary>
    public bool IsAvailable(string firmwarePath) => TryResolveToolPath(firmwarePath, out _);

    /// <summary>
    /// Resolves the flash tool for a firmware path: the path itself when it is the tool, otherwise
    /// the first match found beneath it.
    /// </summary>
    public bool TryResolveToolPath(string firmwarePath, [NotNullWhen(true)] out string? toolPath)
    {
        toolPath = null;

        if (string.IsNullOrWhiteSpace(firmwarePath))
        {
            return false;
        }

        if (File.Exists(firmwarePath))
        {
            toolPath = firmwarePath;
            return true;
        }

        if (!Directory.Exists(firmwarePath))
        {
            return false;
        }

        try
        {
            var matches = Directory.GetFiles(firmwarePath, _toolFileName, SearchOption.AllDirectories);
            if (matches.Length == 0)
            {
                return false;
            }

            toolPath = matches[0];
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable tree is indistinguishable from a missing tool for availability purposes.
            return false;
        }
    }
}
