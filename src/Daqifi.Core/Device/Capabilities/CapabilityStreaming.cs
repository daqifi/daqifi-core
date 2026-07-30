using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>streaming</c> block of the capability document.
/// </summary>
/// <remarks>
/// The three rate figures are three different contracts and are not interchangeable:
/// <list type="bullet">
/// <item><description>
/// <see cref="MaximumSampleRateHz"/> is the hardware envelope — the sampling ISR's absolute
/// ceiling, not achievable under real load. It is the board's true upper bound and is what
/// <see cref="DeviceCapabilities.MaxSamplingRate"/> is populated from.
/// </description></item>
/// <item><description>
/// <see cref="ConservativeEnvelopeHz"/> is guaranteed drop-free for <i>any</i> configuration. A
/// client that always picks this rate is never surprised.
/// </description></item>
/// <item><description>
/// <see cref="CurrentMaximumRateHz"/> is the device's authoritative cap for the channel set
/// enabled <i>right now</i>, on the interface and encoding in use. It changes whenever the enabled
/// set changes, so it is only as fresh as the last document read.
/// </description></item>
/// </list>
/// </remarks>
public sealed class CapabilityStreaming
{
    /// <summary>Gets the lowest sample rate in Hz the device accepts, or <c>null</c> when not stated.</summary>
    public int? MinimumSampleRateHz { get; init; }

    /// <summary>
    /// Gets the absolute sampling-ISR ceiling in Hz, or <c>null</c> when not stated. The hardware
    /// envelope, not a rate that is achievable with every channel set.
    /// </summary>
    public int? MaximumSampleRateHz { get; init; }

    /// <summary>
    /// Gets the rate in Hz the device guarantees drop-free regardless of channel selection,
    /// interface, or encoding; or <c>null</c> when not stated.
    /// </summary>
    public int? ConservativeEnvelopeHz { get; init; }

    /// <summary>
    /// Gets the device's authoritative cap in Hz for the channel set enabled at the moment the
    /// document was read, or <c>null</c> when not stated. The firmware reports <c>0</c> when no
    /// channels are enabled; that <c>0</c> is preserved here rather than being treated as absent,
    /// because it is a real answer ("nothing to stream") and not a missing field.
    /// </summary>
    public int? CurrentMaximumRateHz { get; init; }

    /// <summary>
    /// Gets how the device handles a start request above <see cref="CurrentMaximumRateHz"/>.
    /// <c>"error"</c> — the firmware's behavior since v3.5.0 — means the start is rejected outright
    /// with SCPI <c>-222</c> and streaming does not begin; there is no silent clamping.
    /// </summary>
    public string? RateValidation { get; init; }

    /// <summary>Gets the device's rate-prediction formula, or <c>null</c> when not stated.</summary>
    public CapabilityRateModel? RateModel { get; init; }

    /// <summary>Gets the stream encodings the device supports, e.g. <c>pb</c>, <c>csv</c>, <c>json</c>.</summary>
    public IReadOnlyList<string> Encodings { get; init; } = Array.Empty<string>();

    /// <summary>Gets the destinations the device can stream to, e.g. <c>usb</c>, <c>wifi</c>, <c>sd</c>.</summary>
    public IReadOnlyList<string> Transports { get; init; } = Array.Empty<string>();
}
