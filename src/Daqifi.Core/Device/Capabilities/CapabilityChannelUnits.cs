using Daqifi.Core.Channel;

namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// Copies the unit each analog input advertises in the capability document onto the matching
/// channel, so a sample can say what its number means without the caller having to look the unit up
/// themselves (#501).
/// </summary>
/// <remarks>
/// <para>
/// The document has carried <see cref="CapabilityChannel.Unit"/> since the parser was written and
/// nothing ever read it; this is the consumer.
/// </para>
/// <para>
/// The unit arrives as an <em>identity</em> <see cref="ChannelScaling"/> — a label with no
/// arithmetic — because that is exactly what the device is stating: these readings are volts. A
/// caller who later configures a transducer conversion replaces the whole scaling, unit included,
/// which is correct: once volts have become PSI, keeping the device's "V" would be a lie.
/// </para>
/// </remarks>
internal static class CapabilityChannelUnits
{
    /// <summary>
    /// Applies the document's analog-input units to <paramref name="channels"/>.
    /// </summary>
    /// <param name="channels">The device's channels, typically a snapshot.</param>
    /// <param name="document">The parsed capability document.</param>
    /// <returns>How many channels were given a unit, for logging.</returns>
    internal static int Apply(IReadOnlyList<IChannel>? channels, CapabilityDocument? document)
    {
        if (channels == null || document == null || channels.Count == 0)
        {
            return 0;
        }

        var applied = 0;
        foreach (var channel in channels)
        {
            // Only analog inputs have a unit worth stating, and only a channel that can hold one
            // can be given one.
            if (channel is not IAnalogChannel || channel is not IScaledChannel scaled)
            {
                continue;
            }

            // Never overwrite what a caller configured. This runs again on every capability
            // refresh — the MCP layer re-reads the document after each channel-configuration call —
            // so clobbering here would quietly undo a transducer conversion minutes after it was
            // set.
            if (scaled.Scaling != null)
            {
                continue;
            }

            var unit = FindAnalogInputUnit(document, channel.ChannelNumber);
            if (unit == null)
            {
                continue;
            }

            scaled.Scaling = ChannelScaling.Identity.WithUnit(unit);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// The unit the document states for analog input <paramref name="channelNumber"/>, or
    /// <c>null</c> when it states none.
    /// </summary>
    /// <remarks>
    /// Matched on <see cref="CapabilityChannel.Kind"/> as well as <see cref="CapabilityChannel.Id"/>
    /// because the document numbers analog inputs and digital pins from 0 independently, so an id
    /// alone is ambiguous — the same trap <see cref="SampleRateCap"/> documents.
    /// </remarks>
    private static string? FindAnalogInputUnit(CapabilityDocument document, int channelNumber)
    {
        foreach (var candidate in document.Channels)
        {
            if (candidate.Kind == CapabilityChannelKind.AnalogInput && candidate.Id == channelNumber)
            {
                return string.IsNullOrWhiteSpace(candidate.Unit) ? null : candidate.Unit;
            }
        }

        return null;
    }
}
