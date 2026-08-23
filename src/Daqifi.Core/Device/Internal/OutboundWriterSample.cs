#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// What the device's outbound writer looked like at one instant, sampled by the text exchange
    /// to tell a leftover reply from its own (issue #593).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two facts are taken together because neither means anything alone. A write count that
    /// has not moved since the exchange opened says the writer has not put this exchange's command
    /// on the wire — but it says the same thing about an exchange that sends by some other route,
    /// or sends nothing at all, and those have every right to a reply. Pairing it with "the writer
    /// still has work outstanding" narrows it to the case that is actually in question: a command
    /// handed over and not yet written.
    /// </para>
    /// <para>
    /// A sample, never a wait. The exchange reads this and moves on; making it block until the
    /// writer catches up is the fix #593 exists to rule out.
    /// </para>
    /// </remarks>
    /// <param name="StartedWrites">
    /// <see cref="Daqifi.Core.Communication.Producers.IMessageProducer{T}.StartedWriteCount"/> at
    /// the instant of the sample.
    /// </param>
    /// <param name="HasWorkOutstanding">
    /// True when the writer had something queued or part-way to the stream — the inverse of
    /// <see cref="Daqifi.Core.Communication.Producers.IMessageProducer{T}.IsIdle"/>.
    /// </param>
    internal readonly record struct OutboundWriterSample(long StartedWrites, bool HasWorkOutstanding);
}
