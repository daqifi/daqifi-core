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
}