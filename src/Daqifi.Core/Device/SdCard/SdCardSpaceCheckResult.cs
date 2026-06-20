using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// The outcome of evaluating SD-card free space against a planned capture, as produced by
/// <see cref="SdCardSpaceCheck.Evaluate"/>. Carries the structured facts behind a low-space warning so
/// consumers can render their own message or rely on <see cref="Message"/>.
/// </summary>
/// <param name="Storage">The free/total byte counts reported by the device.</param>
/// <param name="EstimatedCaptureBytes">
/// The estimated size of the planned capture in bytes, or <see langword="null"/> if no capture estimate
/// was supplied.
/// </param>
/// <param name="MinimumFreeBytes">The "nearly full" threshold that was applied, in bytes.</param>
/// <param name="IsNearlyFull">
/// <see langword="true"/> when free space is below <paramref name="MinimumFreeBytes"/>.
/// </param>
/// <param name="IsInsufficientForCapture">
/// <see langword="true"/> when a capture estimate was supplied and free space is below it.
/// </param>
/// <param name="EstimatedTimeUntilFull">
/// When a capture estimate with a positive write rate was supplied, the approximate time the card can
/// keep recording before it fills; otherwise <see langword="null"/>.
/// </param>
/// <param name="Message">A human-readable summary of the warning, or <see langword="null"/> when no warning applies.</param>
public sealed record SdCardSpaceCheckResult(
    SdCardStorageInfo Storage,
    long? EstimatedCaptureBytes,
    long MinimumFreeBytes,
    bool IsNearlyFull,
    bool IsInsufficientForCapture,
    TimeSpan? EstimatedTimeUntilFull,
    string? Message)
{
    /// <summary>
    /// <see langword="true"/> when the user should be warned before starting the capture — i.e. the card
    /// is nearly full or the planned capture will not fit.
    /// </summary>
    public bool ShouldWarn => IsNearlyFull || IsInsufficientForCapture;
}
