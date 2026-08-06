using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// What a captured session says should happen on the way back: the channels to re-enable, and
    /// whether the stream itself should be restarted.
    /// </summary>
    /// <remarks>
    /// A plan is a decision, not an action. Nothing here has touched the device — the caller applies
    /// it, in the order that matters, through the device's own members.
    /// </remarks>
    internal readonly struct SessionRestorePlan
    {
        internal SessionRestorePlan(IReadOnlyList<IChannel> channelsToEnable, bool resumeStreaming)
        {
            ChannelsToEnable = channelsToEnable;
            ResumeStreaming = resumeStreaming;
        }

        /// <summary>
        /// Gets the channel objects, drawn from the ones the device has <em>now</em>, that were
        /// enabled when the connection dropped. Empty when none of them survived the reconnect.
        /// </summary>
        public IReadOnlyList<IChannel> ChannelsToEnable { get; }

        /// <summary>
        /// Gets a value indicating whether the interrupted stream should be restarted — true only
        /// when data really was flowing at the drop <em>and</em> the policy allows resuming.
        /// </summary>
        public bool ResumeStreaming { get; }
    }

    /// <summary>
    /// The subset of a streaming session that Core owns and can therefore put back: which channels
    /// were enabled, and whether data was flowing (issue #379).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deciding is separated from applying, the same split <see cref="SessionCommandInterpreter"/>
    /// uses. Capturing the session and working out what restoring it implies are pure operations
    /// over a channel list, pinned directly by <c>StreamingSessionSnapshotTests</c>; the effects
    /// they imply — disabling everything first, sending the enable mask, restarting the stream —
    /// stay on the device, which is the only thing that owns them.
    /// </para>
    /// <para>
    /// The enabled set is held by channel <em>identity</em> (type and number) rather than by
    /// reference, because a reconnect can replace the channel objects wholesale, and a device that
    /// came back reporting a different channel count should restore the intersection rather than
    /// fail.
    /// </para>
    /// </remarks>
    internal sealed class StreamingSessionSnapshot
    {
        private readonly HashSet<(ChannelType Type, int Number)> _enabledChannels;

        private StreamingSessionSnapshot(HashSet<(ChannelType Type, int Number)> enabledChannels, bool wasStreaming)
        {
            _enabledChannels = enabledChannels;
            WasStreaming = wasStreaming;
        }

        /// <summary>Gets a value indicating whether data was flowing when the connection dropped.</summary>
        public bool WasStreaming { get; }

        /// <summary>Gets how many channels were enabled at the drop.</summary>
        public int EnabledChannelCount => _enabledChannels.Count;

        /// <summary>
        /// Records the session as it stands: the identities of every enabled channel in
        /// <paramref name="channels"/>, plus <paramref name="isStreaming"/>.
        /// </summary>
        /// <remarks>
        /// The channel state is copied out immediately rather than held by reference, so a snapshot
        /// keeps describing the instant it was taken even though the caller goes on mutating those
        /// same channel objects — which is exactly what re-initialization after a reconnect does.
        /// </remarks>
        /// <param name="channels">The device's channels at the moment of the drop.</param>
        /// <param name="isStreaming">Whether the device was streaming at the moment of the drop.</param>
        public static StreamingSessionSnapshot Capture(IEnumerable<IChannel> channels, bool isStreaming)
        {
            ArgumentNullException.ThrowIfNull(channels);

            var enabled = new HashSet<(ChannelType, int)>();
            foreach (var channel in channels)
            {
                if (channel.IsEnabled)
                {
                    enabled.Add((channel.Type, channel.ChannelNumber));
                }
            }

            return new StreamingSessionSnapshot(enabled, isStreaming);
        }

        /// <summary>
        /// Works out what putting this session back means for the device as it is <em>now</em>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The enable set is replayed from the snapshot rather than read back off the channel
        /// objects: <see cref="DaqifiDevice.PopulateChannelsFromStatus"/> resyncs analog
        /// <c>IsEnabled</c> from the device's own enabled mask on every status message (#409), so by
        /// the time re-initialization is done the in-memory view reflects the freshly reconnected
        /// device, not the session that was lost.
        /// </para>
        /// <para>
        /// The streaming frequency needs no replay — it is a host-side setting that the drop never
        /// touched — but it does have to reach the device again, which is what the caller's resumed
        /// <see cref="DaqifiStreamingDevice.StartStreaming"/> does.
        /// </para>
        /// </remarks>
        /// <param name="currentChannels">The channels the reconnected device is reporting.</param>
        /// <param name="options">The reconnect policy; <see cref="ReconnectOptions.ResumeStreaming"/> gates the restart.</param>
        /// <returns>The channels to re-enable and whether to restart the stream.</returns>
        public SessionRestorePlan PlanRestore(IEnumerable<IChannel> currentChannels, ReconnectOptions options)
        {
            ArgumentNullException.ThrowIfNull(currentChannels);
            ArgumentNullException.ThrowIfNull(options);

            var toEnable = new List<IChannel>();
            foreach (var channel in currentChannels)
            {
                if (_enabledChannels.Contains((channel.Type, channel.ChannelNumber)))
                {
                    toEnable.Add(channel);
                }
            }

            return new SessionRestorePlan(toEnable, WasStreaming && options.ResumeStreaming);
        }
    }
}
