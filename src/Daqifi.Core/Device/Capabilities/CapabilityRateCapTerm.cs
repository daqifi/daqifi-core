namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// Names one of the limits in <see cref="CapabilityRateCapTerms"/>, as returned by
/// <see cref="CapabilityRateCapTerms.BindingTerm"/>.
/// </summary>
public enum CapabilityRateCapTerm
{
    /// <summary>The ADC throughput limit, <see cref="CapabilityRateCapTerms.AdcAdditiveHz"/>.</summary>
    AdcAdditive,

    /// <summary>The interface and encoding limit, <see cref="CapabilityRateCapTerms.TransportHz"/>.</summary>
    Transport,

    /// <summary>The hardware scan-retrigger limit, <see cref="CapabilityRateCapTerms.ScanBoundHz"/>.</summary>
    ScanBound,

    /// <summary>The SD writer limit, <see cref="CapabilityRateCapTerms.SdAdditiveHz"/>.</summary>
    SdAdditive
}
