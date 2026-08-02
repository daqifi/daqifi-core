using System.Globalization;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Derives a 0-100 progress percentage for the WiFi (WINC) flash from the flash tool's live
/// stdout. The tool runs a fast local image-build phase (whose "written … (NN%)" lines reach
/// 100% and must be ignored, or they latch the bar near the top before the real flash starts)
/// followed by the multi-minute on-device write → read → verify phases. Those phases emit
/// block-address lines like <c>0x000000:[wwwwwwww] 0x008000:[wwwwwwww] …</c> with no percent,
/// so this parser advances the bar from the highest block address seen relative to the flashed
/// range. Each phase occupies its own monotonically increasing band; <see cref="Observe"/>
/// returns the new percent when it advances, or <c>null</c> when a line carries no progress.
/// </summary>
internal sealed class WifiFlashProgressParser
{
    private static readonly Regex BlockAddressRegex = new(
        @"0x(?<addr>[0-9a-fA-F]+)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VerifyRangeRegex = new(
        @"verify range\s+0x(?<start>[0-9a-fA-F]+)\s+to\s+0x(?<end>[0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Block size between consecutive addresses in the tool's progress lines (0x8000).
    private const long BlockSize = 0x8000;

    // Default flashed range when the tool hasn't yet announced "verify range" (the WINC
    // programmed region is 0x80000 = 512 KB); expanded if a larger address is observed.
    private long _totalRange = 0x80000;

    // Base address of the flashed range. Block addresses in the tool output are absolute, so
    // the covered fraction is measured relative to this start (0 unless "verify range" reports
    // a non-zero base).
    private long _rangeStart;

    private Phase _phase = Phase.PreFlash;
    private double _lastPercent;

    private enum Phase
    {
        PreFlash,
        Write,
        Read,
        Verify
    }

    // Per-phase overall bands (write is weighted heaviest — it is by far the longest phase).
    private static (double Start, double End) BandFor(Phase phase) => phase switch
    {
        Phase.Write => (5, 60),
        Phase.Read => (60, 78),
        Phase.Verify => (78, 100),
        _ => (0, 0)
    };

    public double? Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (line.Contains("begin write operation", StringComparison.OrdinalIgnoreCase))
        {
            return Advance(Phase.Write, BandFor(Phase.Write).Start);
        }

        if (line.Contains("begin read operation", StringComparison.OrdinalIgnoreCase))
        {
            return Advance(Phase.Read, BandFor(Phase.Read).Start);
        }

        var verifyRange = VerifyRangeRegex.Match(line);
        if (verifyRange.Success &&
            long.TryParse(verifyRange.Groups["start"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rangeStart) &&
            long.TryParse(verifyRange.Groups["end"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rangeEnd) &&
            rangeEnd > rangeStart)
        {
            _rangeStart = rangeStart;
            _totalRange = rangeEnd - rangeStart;
            return null;
        }

        if (line.Contains("begin verify operation", StringComparison.OrdinalIgnoreCase))
        {
            return Advance(Phase.Verify, BandFor(Phase.Verify).Start);
        }

        // Block-address lines advance the current phase. Ignored before the device flash
        // starts (PreFlash) so the image-build phase never moves the bar.
        if (_phase != Phase.PreFlash)
        {
            var highestAddress = HighestBlockAddress(line);
            if (highestAddress.HasValue)
            {
                // Block addresses are absolute; measure coverage from the range base so a
                // non-zero start doesn't make the fraction saturate to 1 immediately.
                var covered = highestAddress.Value - _rangeStart + BlockSize;
                if (covered > _totalRange)
                {
                    _totalRange = covered;
                }

                var fraction = Math.Clamp(covered / (double)_totalRange, 0, 1);
                var (start, end) = BandFor(_phase);
                return Advance(_phase, start + (fraction * (end - start)));
            }
        }

        return null;
    }

    private double? Advance(Phase phase, double candidatePercent)
    {
        if (phase > _phase)
        {
            _phase = phase;
        }

        var clamped = Math.Clamp(candidatePercent, 0, 100);

        // Monotonic: never let the bar move backward (e.g. address resets to 0 at each new phase).
        if (clamped <= _lastPercent)
        {
            return null;
        }

        _lastPercent = clamped;
        return clamped;
    }

    private static long? HighestBlockAddress(string line)
    {
        long? highest = null;
        foreach (Match match in BlockAddressRegex.Matches(line))
        {
            if (long.TryParse(
                    match.Groups["addr"].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var address))
            {
                if (highest is null || address > highest.Value)
                {
                    highest = address;
                }
            }
        }

        return highest;
    }
}
