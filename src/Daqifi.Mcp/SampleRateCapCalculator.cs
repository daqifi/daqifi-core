namespace Daqifi.Mcp;

/// <summary>
/// Pure sample-rate cap arithmetic shared by <see cref="DaqifiAgent.SetSampleRateAsync"/> and the
/// channel-configuration calls that must re-validate an already-live rate against a cap the
/// channel set just changed (#447). Factored out of <see cref="DaqifiAgent"/> so this logic is
/// testable without a connected device.
/// </summary>
public static class SampleRateCapCalculator
{
    /// <summary>
    /// Computes the effective sample-rate ceiling from the device's board-derived absolute
    /// ceiling, its optional per-channel-set cap, and an optional server-configured clamp.
    /// </summary>
    /// <param name="hardwareMaxSamplingRateHz">
    /// The device's absolute sampling-ISR ceiling (<c>DeviceCapabilities.MaxSamplingRate</c>).
    /// Non-positive values are floored to 1 so the result is never an impossible "at most 0".
    /// </param>
    /// <param name="currentMaxRateHz">
    /// The device's cap for its currently enabled channels
    /// (<c>CapabilityStreaming.CurrentMaximumRateHz</c>), or <c>null</c> when no capability
    /// document has been read. Bounded to <paramref name="hardwareMaxSamplingRateHz"/> — the two
    /// values come from independently-parsed fields, so a stale or racing read could otherwise
    /// report a "current" cap above the absolute ceiling. <c>0</c> is a real answer (no channels
    /// enabled) and is deliberately not floored the way <paramref name="hardwareMaxSamplingRateHz"/> is.
    /// </param>
    /// <param name="maxSampleRateHzOption">The server's optional <c>--max-sample-rate-hz</c> clamp.</param>
    /// <returns>The effective cap in Hz. Can legitimately be <c>0</c> when nothing is enabled.</returns>
    public static int ComputeCapHz(int hardwareMaxSamplingRateHz, int? currentMaxRateHz, int? maxSampleRateHzOption)
    {
        var hardwareMax = Math.Max(1, hardwareMaxSamplingRateHz);
        var deviceCap = currentMaxRateHz.HasValue ? Math.Min(currentMaxRateHz.Value, hardwareMax) : hardwareMax;
        return maxSampleRateHzOption.HasValue ? Math.Min(maxSampleRateHzOption.Value, deviceCap) : deviceCap;
    }

    /// <summary>
    /// Re-validates an already-live rate against a (possibly just-lowered) cap, lowering the rate
    /// to the cap when it no longer fits.
    /// </summary>
    /// <param name="currentRateHz">The rate currently live on the device.</param>
    /// <param name="capHz">The effective cap from <see cref="ComputeCapHz"/>.</param>
    /// <returns>
    /// The rate that should now be live, and — when it differs from <paramref name="currentRateHz"/>
    /// — the rate that was live before the adjustment. A cap of <c>0</c> (nothing enabled) leaves
    /// the rate alone rather than driving it to <c>0</c>, which is not a meaningful streaming
    /// frequency.
    /// </returns>
    public static (int NewRateHz, int? AdjustedFromHz) EnforceCap(int currentRateHz, int capHz)
    {
        if (capHz <= 0 || currentRateHz <= capHz)
        {
            return (currentRateHz, null);
        }

        return (capHz, currentRateHz);
    }
}
