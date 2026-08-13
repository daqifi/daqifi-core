namespace Daqifi.Core.Channel;

/// <summary>
/// Implemented by Core's channel types so the device that owns them can tell when a channel's
/// <see cref="IChannel.IsEnabled"/> state has changed.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one reason: the streaming decode path caches which analog channels are active,
/// and that cache has to be invalidated by <em>every</em> way enablement can change — including a
/// caller writing <c>channel.IsEnabled = true</c> directly, which <see cref="IChannel"/> permits and
/// which no device API sees (issue #490). Without a notification the decoder would happily keep
/// mapping frame values onto the previous set of channels, silently attributing readings to the
/// wrong channel.
/// </para>
/// <para>
/// Deliberately internal. It is an implementation detail of that invalidation, not a contract
/// consumers implement: the device only ever holds channel instances built by
/// <c>StatusChannelPopulator</c> from a device status message, and those are always Core's own
/// <see cref="AnalogChannel"/> and <see cref="DigitalChannel"/>.
/// </para>
/// </remarks>
internal interface IChannelEnablementNotifier
{
    /// <summary>
    /// Raised after <see cref="IChannel.IsEnabled"/> changes to a different value, outside any lock
    /// the channel holds, so a handler is free to read back from the channel.
    /// </summary>
    event Action? EnablementChanged;
}
