using System;
using System.Collections.Generic;
using System.Globalization;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Evaluates SD-card free space against a planned capture and decides whether to warn the user before
/// SD logging starts. This is the client-side UX half of issue #230: the firmware provides a safe
/// mechanism (the <c>SYSTem:STORage:SD:MINFree</c> gate), while the client surfaces the warning.
/// </summary>
/// <remarks>
/// Pure and side-effect free, so it can be unit-tested without a device. The two rules mirror the issue:
/// warn when the planned capture won't fit, and warn when the card is nearly full regardless of estimate.
/// </remarks>
public static class SdCardSpaceCheck
{
    /// <summary>
    /// The default "nearly full" threshold: 100 MB. A capture starting with less free space than this is
    /// flagged regardless of any capture estimate.
    /// </summary>
    public const long DefaultMinimumFreeBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Evaluates the supplied storage info (and optional capture estimate) against the warning rules.
    /// </summary>
    /// <param name="storage">The free/total byte counts reported by the device.</param>
    /// <param name="plannedCapture">
    /// An optional estimate of the upcoming capture. When supplied, the result includes a "won't fit"
    /// check and a truncation ETA.
    /// </param>
    /// <param name="minimumFreeBytes">
    /// The "nearly full" threshold in bytes. Defaults to <see cref="DefaultMinimumFreeBytes"/>.
    /// </param>
    /// <returns>A <see cref="SdCardSpaceCheckResult"/> describing whether and why a warning applies.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storage"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="minimumFreeBytes"/> is negative.</exception>
    public static SdCardSpaceCheckResult Evaluate(
        SdCardStorageInfo storage,
        SdCardCaptureEstimate? plannedCapture = null,
        long minimumFreeBytes = DefaultMinimumFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (minimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes), minimumFreeBytes, "Minimum free space cannot be negative.");
        }

        var free = storage.FreeBytes;
        long? estimatedBytes = plannedCapture?.EstimatedBytes;

        var isNearlyFull = free < minimumFreeBytes;
        var isInsufficient = estimatedBytes.HasValue && free < estimatedBytes.Value;

        TimeSpan? timeUntilFull = null;
        if (plannedCapture is { BytesPerSecond: > 0 })
        {
            // Wall-clock the card can record before it fills, derived from the free space and the
            // estimated write rate. Surfaced primarily as the "truncate after ~N" figure.
            timeUntilFull = TimeSpan.FromSeconds((double)free / plannedCapture.BytesPerSecond);
        }

        var message = BuildMessage(free, estimatedBytes, minimumFreeBytes, isNearlyFull, isInsufficient, timeUntilFull);

        return new SdCardSpaceCheckResult(
            storage,
            estimatedBytes,
            minimumFreeBytes,
            isNearlyFull,
            isInsufficient,
            timeUntilFull,
            message);
    }

    private static string? BuildMessage(
        long freeBytes,
        long? estimatedBytes,
        long minimumFreeBytes,
        bool isNearlyFull,
        bool isInsufficient,
        TimeSpan? timeUntilFull)
    {
        if (!isNearlyFull && !isInsufficient)
        {
            return null;
        }

        var parts = new List<string>(2);

        if (isInsufficient && estimatedBytes.HasValue)
        {
            var truncation = timeUntilFull.HasValue
                ? $", truncating after about {FormatDuration(timeUntilFull.Value)}"
                : string.Empty;
            parts.Add(
                $"The planned capture (~{FormatBytes(estimatedBytes.Value)}) will not fit in the {FormatBytes(freeBytes)} free on the SD card{truncation}.");
        }

        if (isNearlyFull)
        {
            parts.Add(
                $"The SD card is nearly full ({FormatBytes(freeBytes)} free, below the {FormatBytes(minimumFreeBytes)} minimum); consider freeing space first.");
        }

        return string.Join(" ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;

        if (bytes >= gib)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / gib:0.##} GB");
        }
        if (bytes >= mib)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / mib:0.##} MB");
        }
        if (bytes >= kib)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / kib:0.##} KB");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalHours:0.#} hours");
        }
        if (duration.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0.#} minutes");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.#} seconds");
    }
}
