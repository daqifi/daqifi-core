namespace Daqifi.Core.Firmware;

/// <summary>
/// Process execution request for <see cref="IExternalProcessRunner"/>.
/// </summary>
public sealed class ExternalProcessRequest
{
    /// <summary>
    /// Gets or sets the executable to start.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets command-line arguments.
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional process working directory.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the maximum execution duration.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets a callback for each stdout line.
    /// </summary>
    public Action<string>? OnStandardOutputLine { get; set; }

    /// <summary>
    /// Gets or sets a callback for each stderr line.
    /// </summary>
    public Action<string>? OnStandardErrorLine { get; set; }

    /// <summary>
    /// Gets or sets a responder callback that can write to stdin.
    /// Return <c>null</c> for no input.
    /// </summary>
    public Func<string, string?>? StandardInputResponseFactory { get; set; }
}
