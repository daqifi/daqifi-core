namespace Daqifi.Core.Channel.Internal;

/// <summary>
/// Shared implementation of the <see cref="IChannelEnablementNotifier"/> contract that
/// <see cref="AnalogChannel"/> and <see cref="DigitalChannel"/> each implemented identically for
/// their subscriber list and their <c>IsEnabled</c> setter.
/// </summary>
/// <remarks>
/// The three members below are one mechanism, not three conveniences, which is why they live
/// together: the subscriber list is mutated under the channel's lock, snapshotted under that same
/// lock, and then invoked with the lock released. Split across two channel classes, that ordering
/// was two copies of an invariant with nothing holding them in agreement — the digital copy's own
/// comment pointed the reader at the analog one for the explanation.
/// <para>
/// Taking the backing fields by <c>ref</c> rather than through accessor delegates keeps this
/// allocation-free, matching the sibling <see cref="ActiveSampleAssignment"/> helper.
/// </para>
/// </remarks>
internal static class ChannelEnablementNotification
{
    /// <summary>
    /// Adds <paramref name="handler"/> to the channel's subscriber list under
    /// <paramref name="syncLock"/>.
    /// </summary>
    /// <param name="syncLock">The lock guarding the channel's state.</param>
    /// <param name="handlers">The channel's subscriber-list backing field.</param>
    /// <param name="handler">The handler being subscribed.</param>
    public static void AddHandler(object syncLock, ref Action? handlers, Action? handler)
    {
        lock (syncLock)
        {
            handlers += handler;
        }
    }

    /// <summary>
    /// Removes <paramref name="handler"/> from the channel's subscriber list under
    /// <paramref name="syncLock"/>.
    /// </summary>
    /// <param name="syncLock">The lock guarding the channel's state.</param>
    /// <param name="handlers">The channel's subscriber-list backing field.</param>
    /// <param name="handler">The handler being unsubscribed.</param>
    public static void RemoveHandler(object syncLock, ref Action? handlers, Action? handler)
    {
        lock (syncLock)
        {
            handlers -= handler;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="isEnabled"/> under
    /// <paramref name="syncLock"/> and notifies subscribers if — and only if — that changed it.
    /// </summary>
    /// <param name="syncLock">The lock guarding the channel's state.</param>
    /// <param name="value">The value being assigned to the channel's <c>IsEnabled</c>.</param>
    /// <param name="isEnabled">The channel's enabled-flag backing field.</param>
    /// <param name="handlers">The channel's subscriber-list backing field.</param>
    /// <remarks>
    /// A write that does not change the value raises nothing: status messages re-assert the same
    /// enabled mask constantly, and treating each one as a change would throw away the caches this
    /// notification exists to keep valid.
    /// <para>
    /// Subscribers are raised outside the lock: the owning device's handler is free to read back
    /// from the channel, and holding the lock across it would make that a self-deadlock waiting to
    /// happen. The subscriber list is captured inside, so a concurrent unsubscribe cannot tear it.
    /// </para>
    /// </remarks>
    public static void SetAndNotify(object syncLock, bool value, ref bool isEnabled, ref Action? handlers)
    {
        Action? subscribers;
        lock (syncLock)
        {
            if (isEnabled == value)
            {
                return;
            }

            isEnabled = value;
            subscribers = handlers;
        }

        subscribers?.Invoke();
    }
}
