using System;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The device's published streaming-rate formula, so a client can preview the ceiling for a
/// hypothetical channel selection without a round-trip per checkbox.
/// </summary>
/// <remarks>
/// The constants come from the firmware's own compile-time streaming budget. The result is an
/// <i>optimistic</i> preview: it accounts for channel count and channel type only, so the rate the
/// device will actually accept for a committed configuration
/// (<see cref="CapabilityStreaming.CurrentMaximumRateHz"/>) can be lower once the per-interface,
/// per-encoding transport cap is folded in. Treat this as UI guidance and
/// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> as authoritative.
/// </remarks>
public sealed class CapabilityRateModel
{
    /// <summary>Gets the formula as published by the device, for display and diagnostics.</summary>
    public string? Formula { get; init; }

    /// <summary>Gets the absolute sampling-ISR ceiling in Hz, or <c>null</c> when not stated.</summary>
    public int? AbsoluteMaximumHz { get; init; }

    /// <summary>
    /// Gets the aggregate ceiling in Hz shared by all dedicated-converter ("simultaneous")
    /// channels, or <c>null</c> when not stated.
    /// </summary>
    public int? Type1AggregateMaximumHz { get; init; }

    /// <summary>Gets the per-tick sampling budget in Hz, or <c>null</c> when not stated.</summary>
    public int? PerTickBudgetHz { get; init; }

    /// <summary>
    /// Gets the fixed per-tick cost, expressed in channel-equivalents, that the budget must cover
    /// before any channel is sampled; or <c>null</c> when not stated.
    /// </summary>
    public int? PerTickOverhead { get; init; }

    /// <summary>
    /// Evaluates the device's formula for a hypothetical channel selection.
    /// </summary>
    /// <param name="simultaneousChannelCount">
    /// How many of the selected channels are dedicated-converter channels — count the
    /// <see cref="CapabilityDocument.Channels"/> entries with
    /// <see cref="CapabilityChannel.Kind"/> of <see cref="CapabilityChannelKind.AnalogInput"/> and
    /// <see cref="CapabilityChannel.IsSimultaneous"/> set.
    /// </param>
    /// <param name="totalChannelCount">
    /// How many analog input channels are selected in total. Digital I/O and analog output do not
    /// factor in — digital cost is amortized into <see cref="PerTickOverhead"/>, and analog output
    /// is not streamed.
    /// </param>
    /// <param name="maxRateHz">The predicted ceiling in Hz, when the model can be evaluated.</param>
    /// <returns>
    /// <c>true</c> when the document supplied enough constants to evaluate at least one term of the
    /// formula; otherwise <c>false</c>, and the caller should fall back to
    /// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> or the board-derived maximum.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when either count is negative, or when <paramref name="simultaneousChannelCount"/>
    /// exceeds <paramref name="totalChannelCount"/> — a selection that cannot exist, and one that
    /// would otherwise silently produce a too-high ceiling.
    /// </exception>
    public bool TryComputeMaxRateHz(int simultaneousChannelCount, int totalChannelCount, out int maxRateHz)
    {
        if (simultaneousChannelCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(simultaneousChannelCount), simultaneousChannelCount, "Channel count cannot be negative.");
        }

        if (totalChannelCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalChannelCount), totalChannelCount, "Channel count cannot be negative.");
        }

        if (simultaneousChannelCount > totalChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(simultaneousChannelCount),
                simultaneousChannelCount,
                "The simultaneous channel count cannot exceed the total channel count.");
        }

        int? ceiling = null;

        if (AbsoluteMaximumHz > 0)
        {
            ceiling = AbsoluteMaximumHz.Value;
        }

        // Only a selection that actually includes dedicated-converter channels is constrained by
        // their aggregate budget: dividing by a zero count would be a division by zero, and
        // treating the term as 0 Hz would wrongly cap a muxed-only selection at nothing.
        if (Type1AggregateMaximumHz > 0 && simultaneousChannelCount > 0)
        {
            var type1Term = Type1AggregateMaximumHz.Value / simultaneousChannelCount;
            ceiling = ceiling.HasValue ? Math.Min(ceiling.Value, type1Term) : type1Term;
        }

        if (PerTickBudgetHz > 0 && PerTickOverhead >= 0)
        {
            var divisor = PerTickOverhead!.Value + totalChannelCount;
            if (divisor > 0)
            {
                var budgetTerm = PerTickBudgetHz.Value / divisor;
                ceiling = ceiling.HasValue ? Math.Min(ceiling.Value, budgetTerm) : budgetTerm;
            }
        }

        maxRateHz = ceiling ?? 0;
        return ceiling.HasValue;
    }
}
