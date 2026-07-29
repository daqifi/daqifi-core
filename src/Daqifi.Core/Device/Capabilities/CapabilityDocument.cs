using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// A device's self-description, as returned by <c>CONFigure:CAPabilities:JSON?</c> on firmware
/// v3.5.0 and newer. Parse one with <see cref="CapabilityDocumentParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the live counterpart to <see cref="DeviceCapabilities.FromDeviceType"/>. It does not
/// replace it: the board-derived table stays the bootstrap (it answers before the device has been
/// asked anything, and for firmware that cannot answer at all) and the permanent fallback for
/// anything the document omits. <see cref="MergeInto"/> overlays only the fields the document
/// actually stated — see ADR 0001, <c>docs/adr/0001-firmware-feature-gating.md</c>.
/// </para>
/// <para>
/// Every field is nullable or defaulted on purpose. The firmware's schema rules make additive
/// change the norm — new fields and new channel kinds appear without a schema-version bump — so a
/// property that is <c>null</c> means "this document did not state it", never "the device does not
/// have it".
/// </para>
/// </remarks>
public sealed class CapabilityDocument
{
    /// <summary>
    /// Gets the document's schema version, as carried in the document body. The firmware bumps
    /// this only on a breaking change; it is the same value
    /// <c>CONFigure:CAPabilities:APIVersion?</c> returns.
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the schema URI the document declares, or <c>null</c> when not stated.</summary>
    public string? SchemaUri { get; init; }

    /// <summary>Gets the device identity block, or <c>null</c> when the document omitted it.</summary>
    public CapabilityIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the device's channels — analog inputs, analog outputs and digital I/O in one flat
    /// list. Empty when the document omitted the array.
    /// </summary>
    public IReadOnlyList<CapabilityChannel> Channels { get; init; } = Array.Empty<CapabilityChannel>();

    /// <summary>Gets the streaming block, or <c>null</c> when the document omitted it.</summary>
    public CapabilityStreaming? Streaming { get; init; }

    /// <summary>
    /// Gets whether the board is fitted with SD-card hardware, or <c>null</c> when not stated.
    /// Structural, not runtime: it does not say whether a card is currently inserted.
    /// </summary>
    public bool? SdSupported { get; init; }

    /// <summary>Gets whether the board supports USB, or <c>null</c> when not stated.</summary>
    public bool? UsbSupported { get; init; }

    /// <summary>Gets whether the board supports WiFi, or <c>null</c> when not stated.</summary>
    public bool? WifiSupported { get; init; }

    /// <summary>Gets whether the board supports Ethernet, or <c>null</c> when not stated.</summary>
    public bool? EthernetSupported { get; init; }

    /// <summary>Gets whether the board has a battery fitted, or <c>null</c> when not stated.</summary>
    public bool? BatteryPresent { get; init; }

    /// <summary>Gets whether the board accepts external power, or <c>null</c> when not stated.</summary>
    public bool? ExternalPowerSupported { get; init; }

    /// <summary>
    /// Gets the document exactly as the device emitted it, for diagnostics and support — the
    /// parsed properties above cover the client-actionable subset, not every field in the schema.
    /// </summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>
    /// Counts the channels of one <see cref="CapabilityChannelKind"/>.
    /// </summary>
    /// <param name="kind">The kind to count.</param>
    /// <returns>The number of <see cref="Channels"/> entries of that kind.</returns>
    public int CountChannels(CapabilityChannelKind kind)
    {
        var count = 0;
        for (var i = 0; i < Channels.Count; i++)
        {
            if (Channels[i].Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Overlays this document's values onto board-derived <see cref="DeviceCapabilities"/>,
    /// leaving every field the document did not state untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Merge, not replace. The caller's instance keeps its board-derived values as the floor, so a
    /// device that answers partially — or a firmware whose schema drops a field we used to read —
    /// degrades to the static table rather than to zeros. This is also what preserves the
    /// "<see cref="DeviceType.Unknown"/> means not-yet-known, not absent" rule that
    /// <see cref="DaqifiDevice.Supports"/> depends on: an absent field never turns a
    /// hardware flag off.
    /// </para>
    /// <para>
    /// Two fields are deliberately never overlaid:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="DeviceCapabilities.HasWincWifiModule"/> — the schema carries no chipset
    /// information by design (it publishes what a client can <i>do</i>, not what parts are
    /// fitted), so this stays board-derived.
    /// </description></item>
    /// <item><description>
    /// <see cref="DeviceCapabilities.SupportsStreaming"/> — the firmware emits the
    /// <c>streaming</c> block unconditionally and has no "streaming supported" boolean, so the
    /// block's presence would not be evidence either way.
    /// </description></item>
    /// </list>
    /// <para>
    /// The channel counts are overlaid as a set, and only when <see cref="Channels"/> is non-empty.
    /// The schema defines <c>channels[]</c> as the board's complete channel list, so once it is
    /// present a count of zero for a kind is a real answer (an NQ1 genuinely has no analog
    /// outputs) rather than a gap.
    /// </para>
    /// </remarks>
    /// <param name="capabilities">The board-derived capabilities to overlay onto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <c>null</c>.</exception>
    public void MergeInto(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (SdSupported.HasValue)
        {
            capabilities.HasSdCard = SdSupported.Value;
        }

        if (UsbSupported.HasValue)
        {
            capabilities.HasUsb = UsbSupported.Value;
        }

        if (WifiSupported.HasValue)
        {
            capabilities.HasWiFi = WifiSupported.Value;
        }

        if (Channels.Count > 0)
        {
            capabilities.AnalogInputChannels = CountChannels(CapabilityChannelKind.AnalogInput);
            capabilities.AnalogOutputChannels = CountChannels(CapabilityChannelKind.AnalogOutput);
            capabilities.DigitalChannels = CountChannels(CapabilityChannelKind.DigitalIo);
        }

        // The absolute ISR ceiling, not the current or conservative rate: MaxSamplingRate is the
        // board's upper bound (DaqifiStreamingDevice validates the requested frequency against it),
        // and the other two figures move with the enabled channel set. A client that needs the
        // achievable rate for a specific configuration reads CurrentMaximumRateHz or evaluates
        // RateModel; the device rejects an over-ask outright, so this bound is a sanity check
        // rather than the authority.
        if (Streaming?.MaximumSampleRateHz > 0)
        {
            capabilities.MaxSamplingRate = Streaming.MaximumSampleRateHz.Value;
        }
    }
}
