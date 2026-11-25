using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// The outcome of evaluating SD-card free space against a planned capture, as produced by
/// <see cref="SdCardSpaceCheck.Evaluate"/>. Carries the structured facts behind a low-space warning so
/// consumers can render their own message or rely on <see cref="Message"/>.
/// </summary>
public sealed record SdCardSpaceCheckResult
{
    /// <summary>
    /// Gets the free/total byte counts reported by the device.
    /// </summary>
    public required SdCardStorageInfo Storage { get; init; }

    /// <summary>
    /// Gets the estimated size of the planned capture in bytes, or <see langword="null"/> if no capture estimate
    /// was supplied.
    /// </summary>
    public required long? EstimatedCaptureBytes { get; init; }

    /// <summary>
    /// Gets the "nearly full" threshold that was applied, in bytes.
    /// </summary>
    public required long MinimumFreeBytes { get; init; }

    /// <summary>
    /// <see langword="true"/> when free space is below <see cref="MinimumFreeBytes"/>.
    /// </summary>
    public required bool IsNearlyFull { get; init; }

    /// <summary>
    /// <see langword="true"/> when a capture estimate was supplied and free space is below it.
    /// </summary>
    public required bool IsInsufficientForCapture { get; init; }

    /// <summary>
    /// When a capture estimate with a positive write rate was supplied, the approximate time the card can
    /// keep recording before it fills; otherwise <see langword="null"/>.
    /// </summary>
    public required TimeSpan? EstimatedTimeUntilFull { get; init; }

    /// <summary>
    /// A human-readable summary of the warning, or <see langword="null"/> when no warning applies.
    /// </summary>
    public required string? Message { get; init; }

    /// <summary>
    /// <see langword="true"/> when the user should be warned before starting the capture — i.e. the card
    /// is nearly full or the planned capture will not fit.
    /// </summary>
    public bool ShouldWarn => IsNearlyFull || IsInsufficientForCapture;
}
