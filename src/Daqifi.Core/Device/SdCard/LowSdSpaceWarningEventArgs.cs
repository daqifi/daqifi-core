using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Provides data for the low-SD-free-space warning raised before SD-card logging starts.
/// Subscribers (e.g. a desktop client) can surface a confirmable dialog and let the user proceed.
/// </summary>
public sealed class LowSdSpaceWarningEventArgs : EventArgs
{
    /// <summary>
    /// Gets the evaluated space-check result behind the warning, including the device storage figures,
    /// the capture estimate (if any), and a human-readable <see cref="SdCardSpaceCheckResult.Message"/>.
    /// </summary>
    public SdCardSpaceCheckResult Result { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LowSdSpaceWarningEventArgs"/> class.
    /// </summary>
    /// <param name="result">The evaluated space-check result. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is <see langword="null"/>.</exception>
    public LowSdSpaceWarningEventArgs(SdCardSpaceCheckResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
