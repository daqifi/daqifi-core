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
    /// Whether the flash tool is present for the given firmware path. Total by design — a probe
    /// answers yes or no, so an unreadable tree reports <c>false</c> rather than throwing.
    /// </summary>
    /// <remarks>
    /// Use <see cref="TryResolveToolPath"/> when a caller is about to act on the answer and needs
    /// to tell "not there" apart from "could not look".
    /// </remarks>
    public bool IsAvailable(string firmwarePath)
    {
        try
        {
            return TryResolveToolPath(firmwarePath, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the flash tool for a firmware path: the path itself when it is the tool, otherwise
    /// the first match found beneath it.
    /// </summary>
    /// <returns><c>true</c> when the tool was found.</returns>
    /// <exception cref="IOException">The directory tree could not be searched.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory tree could not be read.</exception>
    /// <remarks>
    /// A tree that exists but cannot be read propagates rather than returning <c>false</c>.
    /// Collapsing it into "not found" is what produces the genuinely misleading
    /// "could not locate the tool — WiFi flashing is Windows-only" message on a machine where the
    /// tool is sitting right there behind a permissions problem.
    /// </remarks>
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

        var matches = Directory.GetFiles(firmwarePath, _toolFileName, SearchOption.AllDirectories);
        if (matches.Length == 0)
        {
            return false;
        }

        toolPath = matches[0];
        return true;
    }
}
