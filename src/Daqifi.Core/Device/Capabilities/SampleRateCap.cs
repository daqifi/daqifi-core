using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The sample-rate ceiling for the channel set a device has enabled <i>right now</i>, and the rule
/// for a live rate that no longer fits under it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeviceCapabilities.MaxSamplingRate"/> — the bound
/// <see cref="IStreamingDevice.StreamingFrequency"/> validates against — is the board's absolute
/// sampling-ISR ceiling, reachable only with almost nothing enabled. The rate a given channel
/// selection can actually sustain is lower, and moves every time the selection changes. Asking for
/// more than that is not a soft failure: since firmware v3.5.0 the device answers a start request
/// above its current maximum with SCPI <c>-222</c> and streams nothing
/// (<see cref="CapabilityStreaming.RateValidation"/>), which surfaces to a client as a session that
/// simply produces no samples.
/// </para>
/// <para>
/// This type is the one place that decides the ceiling, so every consumer of Core gets the same
/// answer. It was previously implemented in the MCP server alone, which left the desktop
/// application and every other library consumer without it (#481).
/// </para>
/// <para>
/// <b>Freshness.</b> The device's own answer (<see cref="CapabilityStreaming.CurrentMaximumRateHz"/>)
/// describes the set that was enabled when the capability document was read, so a caller that
/// changes the enabled set should re-read it — <see cref="DaqifiDevice.ReadCapabilityDocumentAsync"/>
/// — before trusting the cap again. When the device stated no cap at all, the published rate model
/// (<see cref="CapabilityRateModel"/>) is evaluated against the set enabled right now instead; it is
/// an optimistic prediction, which is why it is a fallback rather than an override.
/// </para>
/// </remarks>
public static class SampleRateCap
{
    /// <summary>
    /// Combines the three rate sources into the effective ceiling in Hz.
    /// </summary>
    /// <param name="hardwareMaximumRateHz">
    /// The board's absolute sampling-ISR ceiling (<see cref="DeviceCapabilities.MaxSamplingRate"/>).
    /// Non-positive values are floored to 1: it is a mutable, unvalidated property, and an
    /// uninitialized 0 would otherwise produce an impossible "at most 0 Hz" that rejects every rate.
    /// </param>
    /// <param name="deviceReportedCapHz">
    /// The device's own cap for the channels enabled when the capability document was read
    /// (<see cref="CapabilityStreaming.CurrentMaximumRateHz"/>), or <c>null</c> when no document has
    /// been read. Authoritative when present, and bounded to
    /// <paramref name="hardwareMaximumRateHz"/> — the two come from independently-parsed fields, so
    /// a stale or racing read could otherwise claim a cap above the absolute ceiling. <c>0</c> is a
    /// real answer ("nothing is enabled, so there is no capacity") and is deliberately not floored
    /// the way the hardware maximum is. A negative value is not a real answer — the JSON parser
    /// accepts any <c>int32</c>, so a malformed document could otherwise both misreport as "nothing
    /// enabled" and, worse, disable every <c>cap &gt; 0</c> guard downstream — and is treated as
    /// absent instead.
    /// </param>
    /// <param name="modelCapHz">
    /// The prediction from the device's published rate model for the channels enabled right now, or
    /// <c>null</c> when there is no model or it could not be evaluated. Used only when
    /// <paramref name="deviceReportedCapHz"/> is absent; see the remarks on this class for why the
    /// device's own answer outranks it.
    /// </param>
    /// <returns>The effective cap in Hz. Can legitimately be <c>0</c> when nothing is enabled.</returns>
    public static int Compute(int hardwareMaximumRateHz, int? deviceReportedCapHz, int? modelCapHz)
    {
        var hardwareMaximum = Math.Max(1, hardwareMaximumRateHz);

        if (deviceReportedCapHz is >= 0)
        {
            return Math.Min(deviceReportedCapHz.Value, hardwareMaximum);
        }

        if (modelCapHz is >= 0)
        {
            return Math.Min(modelCapHz.Value, hardwareMaximum);
        }

        return hardwareMaximum;
    }

    /// <summary>
    /// Computes the effective cap in Hz for <paramref name="device"/> as it is configured right now.
    /// </summary>
    /// <param name="device">The device to compute the cap for.</param>
    /// <returns>
    /// <para>
    /// The effective cap in Hz. <c>0</c> when the device has no analog inputs enabled and either
    /// said so itself or published a rate model to be evaluated against the enabled set.
    /// </para>
    /// <para>
    /// A device that has stated neither — no capability document, or one carrying no
    /// <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> and no
    /// <see cref="CapabilityStreaming.RateModel"/> — has said nothing about how its channel set
    /// affects the rate, so it reports the board ceiling whatever is enabled, exactly as it did
    /// before this cap existed. Reporting <c>0</c> for those devices would newly refuse rates they
    /// accept today, on no evidence.
    /// </para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <c>null</c>.</exception>
    public static int ComputeForDevice(IStreamingDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var metadata = device.Metadata;
        var streaming = metadata.CapabilityDocument?.Streaming;
        var deviceReportedCapHz = streaming?.CurrentMaximumRateHz;

        int? modelCapHz = null;

        // Evaluated only when the device itself stated nothing: the model accounts for channel count
        // and channel type only, so it can sit above the cap the device would actually enforce once
        // the per-interface, per-encoding transport cost is folded in. Skipping it in the common
        // case also skips walking the channel list on every read.
        if (deviceReportedCapHz is not >= 0 && streaming?.RateModel is { } model)
        {
            var (simultaneousCount, totalCount) = CountEnabledAnalogInputs(
                device.GetChannelsSnapshot(), metadata.CapabilityDocument!);

            if (totalCount == 0)
            {
                // No analog input is enabled, so there is no sampling cadence to set a rate for.
                // That includes a digital-only selection: digital pins are captured on the analog
                // sample tick rather than driving one of their own, which is why the model counts
                // only analog inputs — and why the device answers 0 for a digital-only selection
                // just as it does for an empty one. Both measured on an NQ1 running firmware 3.7.2.
                //
                // Evaluating the model here would disagree with the device: its formula keeps a
                // fixed per-tick overhead term at zero channels, so an empty selection comes back
                // as a healthy rate (18,333 Hz on that board) and every "cap is 0, so nothing is
                // enabled" check downstream reads it as a live configuration.
                modelCapHz = 0;
            }
            else if (model.TryComputeMaxRateHz(simultaneousCount, totalCount, out var predictedHz))
            {
                modelCapHz = predictedHz;
            }
        }

        return Compute(metadata.Capabilities.MaxSamplingRate, deviceReportedCapHz, modelCapHz);
    }

    /// <summary>
    /// Applies a cap to a rate that is already live, lowering it when it no longer fits.
    /// </summary>
    /// <param name="currentRateHz">The rate currently live on the device.</param>
    /// <param name="capHz">The effective cap, from <see cref="Compute"/> or <see cref="ComputeForDevice"/>.</param>
    /// <returns>
    /// The rate that should now be live, and — when it differs from <paramref name="currentRateHz"/>
    /// — the rate that was live before the adjustment. A cap of <c>0</c> (nothing enabled) leaves the
    /// rate alone rather than driving it to <c>0</c>, which is not a meaningful streaming frequency:
    /// the rate is stale until the channel set changes again, not invalid.
    /// </returns>
    public static (int NewRateHz, int? AdjustedFromHz) Enforce(int currentRateHz, int capHz)
    {
        if (capHz <= 0 || currentRateHz <= capHz)
        {
            return (currentRateHz, null);
        }

        return (capHz, currentRateHz);
    }

    /// <summary>
    /// Lowers <paramref name="device"/>'s <see cref="IStreamingDevice.StreamingFrequency"/> to the
    /// cap its current channel set allows, when the live rate no longer fits under it.
    /// </summary>
    /// <remarks>
    /// The case this exists for: a rate set while one channel was enabled stays live after fifteen
    /// more are enabled, is echoed back as if it were still valid, and is not even re-settable —
    /// re-requesting the same value now fails (#447).
    /// </remarks>
    /// <param name="device">The device whose live rate should be re-validated.</param>
    /// <returns>The rate that was live before the adjustment, or <c>null</c> when none was needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <c>null</c>.</exception>
    public static int? EnforceOn(IStreamingDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var (newRateHz, adjustedFromHz) = Enforce(device.StreamingFrequency, ComputeForDevice(device));
        if (adjustedFromHz.HasValue)
        {
            device.StreamingFrequency = newRateHz;
        }

        return adjustedFromHz;
    }

    /// <summary>
    /// Counts the enabled analog <i>input</i> channels, and how many of those the capability
    /// document marks as dedicated-converter ("simultaneous") channels — the two figures
    /// <see cref="CapabilityRateModel.TryComputeMaxRateHz"/> takes.
    /// </summary>
    /// <remarks>
    /// Digital I/O is excluded because its cost is amortized into the model's per-tick overhead, and
    /// analog outputs because they are not streamed. Both exclusions are the model's own contract,
    /// not an approximation.
    /// </remarks>
    private static (int SimultaneousCount, int TotalCount) CountEnabledAnalogInputs(
        IReadOnlyList<IChannel> channels, CapabilityDocument document)
    {
        var simultaneousCount = 0;
        var totalCount = 0;

        foreach (var channel in channels)
        {
            if (channel.Type != ChannelType.Analog
                || channel.Direction != ChannelDirection.Input
                || !channel.IsEnabled)
            {
                continue;
            }

            totalCount++;

            if (IsSimultaneous(document, channel.ChannelNumber))
            {
                simultaneousCount++;
            }
        }

        return (simultaneousCount, totalCount);
    }

    /// <summary>
    /// Reports whether the document describes analog input <paramref name="channelNumber"/> as a
    /// dedicated-converter channel. Matched on <see cref="CapabilityChannel.Kind"/> as well as
    /// <see cref="CapabilityChannel.Id"/> because the document numbers analog inputs and digital
    /// pins from 0 independently, so an id alone is ambiguous.
    /// </summary>
    private static bool IsSimultaneous(CapabilityDocument document, int channelNumber)
    {
        foreach (var candidate in document.Channels)
        {
            if (candidate.Kind == CapabilityChannelKind.AnalogInput && candidate.Id == channelNumber)
            {
                return candidate.IsSimultaneous;
            }
        }

        return false;
    }
}
