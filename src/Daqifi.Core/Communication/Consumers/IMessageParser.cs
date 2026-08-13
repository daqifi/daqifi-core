using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Interface for parsing raw data into structured messages.
/// </summary>
/// <typeparam name="T">The type of message data to parse.</typeparam>
public interface IMessageParser<T>
{
    /// <summary>
    /// Parses raw data into complete messages.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed messages.</returns>
    IEnumerable<IInboundMessage<T>> ParseMessages(byte[] data, out int consumedBytes);

    /// <summary>
    /// Parses raw data into complete messages without requiring the caller to own an array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so a consumer that accumulates bytes in a buffer can hand the parser a view
    /// over that buffer instead of copying it out first. <see cref="StreamMessageConsumer{T}"/>
    /// calls it on every read, so on a device streaming at kilohertz rates the copy it avoids is
    /// the whole wire throughput, duplicated as garbage (issue #490).
    /// </para>
    /// <para>
    /// The default implementation copies the span into an array and defers to
    /// <see cref="ParseMessages(byte[], out int)"/>, so existing parsers keep working unchanged and
    /// are no slower than they were. Parsers on a hot path should override it; the span must not be
    /// captured beyond the call, as the caller may reuse or resize the underlying storage
    /// immediately afterwards.
    /// </para>
    /// </remarks>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed messages.</returns>
    IEnumerable<IInboundMessage<T>> ParseMessages(ReadOnlySpan<byte> data, out int consumedBytes)
        => ParseMessages(data.ToArray(), out consumedBytes);
}