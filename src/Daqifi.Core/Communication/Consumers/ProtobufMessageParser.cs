using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Message parser for binary protobuf messages.
/// Uses CodedInputStream to properly track consumed bytes and handle variable-length messages.
/// </summary>
public class ProtobufMessageParser : IMessageParser<DaqifiOutMessage>
{
    private const int MaxRetryAttempts = 3;
    
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
        var retryCount = 0;
        
        while (currentIndex < data.Length && retryCount < MaxRetryAttempts)
        {
            try
            {
                // Use CodedInputStream for proper byte tracking
                var remainingData = new ReadOnlySpan<byte>(data, currentIndex, data.Length - currentIndex);
                var codedInput = new CodedInputStream(remainingData.ToArray());
                
                // Record the position before parsing
                var startPosition = codedInput.Position;
                
                // Try to parse a protobuf message
                var message = DaqifiOutMessage.Parser.ParseFrom(codedInput);
                
                // Calculate actual bytes consumed by the parser
                var bytesConsumed = codedInput.Position - startPosition;
                
                if (bytesConsumed > 0)
                {
                    currentIndex += (int)bytesConsumed;
                    consumedBytes = currentIndex;
                    messages.Add(new ProtobufMessage(message));
                    retryCount = 0; // Reset retry count on successful parse
                }
                else
                {
                    // If no bytes were consumed, we might be stuck - advance by 1
                    currentIndex++;
                    consumedBytes = currentIndex;
                    retryCount++;
                }
            }
            catch (InvalidProtocolBufferException)
            {
                // If we can't parse a complete message, stop parsing
                // This is expected when we don't have a complete message
                break;
            }
            catch (Exception)
            {
                // For other exceptions, advance by one byte and retry
                currentIndex++;
                consumedBytes = currentIndex;
                retryCount++;
            }
        }

        return messages;
    }
}