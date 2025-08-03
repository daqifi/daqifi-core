using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Message parser for binary protobuf messages.
/// Handles variable-length protobuf messages by detecting message boundaries.
/// </summary>
public class ProtobufMessageParser : IMessageParser<DaqifiOutMessage>
{
    /// <summary>
    /// Parses raw data into protobuf messages.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed protobuf messages.</returns>
    public IEnumerable<IInboundMessage<DaqifiOutMessage>> ParseMessages(byte[] data, out int consumedBytes)
    {
        var messages = new List<IInboundMessage<DaqifiOutMessage>>();
        consumedBytes = 0;

        if (data.Length == 0)
            return messages;

        var currentIndex = 0;
        while (currentIndex < data.Length)
        {
            try
            {
                // Try to parse a protobuf message from the current position
                var remainingData = new byte[data.Length - currentIndex];
                Array.Copy(data, currentIndex, remainingData, 0, remainingData.Length);

                var message = DaqifiOutMessage.Parser.ParseFrom(remainingData);
                
                // Calculate how many bytes were consumed for this message
                var messageBytes = message.CalculateSize();
                currentIndex += messageBytes;
                consumedBytes = currentIndex;

                messages.Add(new ProtobufMessage(message));
            }
            catch (InvalidProtocolBufferException)
            {
                // If we can't parse a complete message, we need more data
                break;
            }
            catch (Exception)
            {
                // For other exceptions, skip one byte and try again
                currentIndex++;
                consumedBytes = currentIndex;
            }
        }

        return messages;
    }
}