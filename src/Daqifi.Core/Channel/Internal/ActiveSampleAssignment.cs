namespace Daqifi.Core.Channel.Internal;

/// <summary>
/// Shared implementation of the "set the active sample under a lock, then raise
/// <see cref="IChannel.SampleReceived"/> outside it" sequence that
/// <see cref="AnalogChannel"/>, <see cref="DigitalChannel"/>, and <see cref="AnalogOutputChannel"/>
/// each implement identically for their <c>SetActiveSample(IDataSample)</c> overload.
/// </summary>
/// <remarks>
/// The event is raised outside the lock so a subscriber that reads back from the channel (e.g. the
/// owning device's handler) cannot self-deadlock against a concurrent reader/writer of the same
/// lock; the null-checked, already-built sample passed to the handler is what was stored, so a
/// concurrent unsubscribe can't tear the notification.
/// </remarks>
internal static class ActiveSampleAssignment
{
    /// <summary>
    /// Validates <paramref name="sample"/>, stores it via <paramref name="store"/> under
    /// <paramref name="syncLock"/>, then invokes <paramref name="handler"/> (if any) outside the lock.
    /// </summary>
    /// <param name="channel">The channel to report as the event's <see cref="SampleReceivedEventArgs.Channel"/>.</param>
    /// <param name="syncLock">The lock guarding the channel's active-sample field.</param>
    /// <param name="sample">The sample to store; must not be <see langword="null"/>.</param>
    /// <param name="store">Stores <paramref name="sample"/> into the channel's backing field.</param>
    /// <param name="handler">The channel's <c>SampleReceived</c> subscriber list, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    public static void Apply(
        IChannel channel,
        object syncLock,
        IDataSample sample,
        Action<IDataSample> store,
        EventHandler<SampleReceivedEventArgs>? handler)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (syncLock)
        {
            store(sample);
        }

        handler?.Invoke(channel, new SampleReceivedEventArgs(channel, sample));
    }
}
