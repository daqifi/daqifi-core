namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>streaming.cap_terms</c> block of the capability document: the individual limits that
/// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> is the minimum of. Published by firmware
/// v3.9.0 and newer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> says how fast the enabled channel set can
/// stream; these say <i>why</i> — whether the transport, the ADC, the scan retrigger or the SD
/// writer is the limit — and so how far a different configuration could go. Use
/// <see cref="BindingTerm"/> to name the one that binds.
/// </para>
/// <para>
/// <b><c>0</c> means "not applicable to this configuration", never a cap of 0 Hz.</b> The firmware
/// floors every real term at 1 Hz, so a consumer can take the minimum of the non-zero terms without
/// a separate applicability flag, and that minimum equals
/// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> from the same document. Every term is
/// <c>0</c> when no input is enabled. Like the rest of the document, a <c>null</c> term means the
/// document did not state it.
/// </para>
/// </remarks>
public sealed class CapabilityRateCapTerms
{
    /// <summary>
    /// Gets the ADC throughput limit in Hz for the enabled channel set, <c>0</c> when not
    /// applicable, or <c>null</c> when not stated.
    /// </summary>
    public int? AdcAdditiveHz { get; init; }

    /// <summary>
    /// Gets the limit in Hz of the interface and encoding in use (USB, WiFi or SD; protobuf, CSV or
    /// JSON), <c>0</c> when not applicable, or <c>null</c> when not stated.
    /// </summary>
    public int? TransportHz { get; init; }

    /// <summary>
    /// Gets the hardware scan-retrigger limit in Hz, <c>0</c> when no multiplexed scan is armed, or
    /// <c>null</c> when not stated. Unlike the other terms, which are fitted throughput envelopes,
    /// this is a hardware safety bound.
    /// </summary>
    public int? ScanBoundHz { get; init; }

    /// <summary>
    /// Gets the limit in Hz of the SD writer competing with the scan, <c>0</c> when not applicable,
    /// or <c>null</c> when not stated. Non-zero only when streaming to the SD card alone.
    /// </summary>
    public int? SdAdditiveHz { get; init; }

    /// <summary>
    /// Gets the term that sets <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> — the lowest
    /// term above zero — or <c>null</c> when no term applies (nothing is enabled, or the document
    /// stated none). When two terms tie, the first in <see cref="CapabilityRateCapTerm"/> order is
    /// returned.
    /// </summary>
    public CapabilityRateCapTerm? BindingTerm
    {
        get
        {
            CapabilityRateCapTerm? binding = null;
            var bindingHz = 0;
            Consider(AdcAdditiveHz, CapabilityRateCapTerm.AdcAdditive, ref binding, ref bindingHz);
            Consider(TransportHz, CapabilityRateCapTerm.Transport, ref binding, ref bindingHz);
            Consider(ScanBoundHz, CapabilityRateCapTerm.ScanBound, ref binding, ref bindingHz);
            Consider(SdAdditiveHz, CapabilityRateCapTerm.SdAdditive, ref binding, ref bindingHz);
            return binding;
        }
    }

    private static void Consider(
        int? termHz,
        CapabilityRateCapTerm term,
        ref CapabilityRateCapTerm? binding,
        ref int bindingHz)
    {
        // Not positive is "not applicable" (0) or not a real answer; strictly lower keeps the
        // first of two equal terms.
        if (termHz > 0 && (binding == null || termHz.Value < bindingHz))
        {
            binding = term;
            bindingHz = termHz.Value;
        }
    }
}
