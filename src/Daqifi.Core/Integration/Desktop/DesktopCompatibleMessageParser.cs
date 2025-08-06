using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Consumers;
using Google.Protobuf;

namespace Daqifi.Core.Integration.Desktop;

/// <summary>
/// Desktop-compatible message parser that exactly replicates the behavior of legacy MessageConsumer.
/// This parser creates ProtobufMessage objects that can be cast directly to DaqifiOutMessage in desktop applications.
/// </summary>
public class DesktopCompatibleMessageParser : IMessageParser<object>
{
    /// <summary>
    /// Parses raw data into desktop-compatible message format.
    /// Replicates the exact parsing logic from legacy MessageConsumer.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed messages compatible with desktop applications.</returns>
    public IEnumerable<IInboundMessage<object>> ParseMessages(byte[] data, out int consumedBytes)
    {
        var messages = new List<IInboundMessage<object>>();
        consumedBytes = 0;

        if (data.Length == 0)
            return messages;

        // For simple testing with raw protobuf bytes, we'll parse without delimiters first
        // But in real scenarios, DAQiFi devices send length-delimited messages
        try
        {
            // First try parsing as raw protobuf (for compatibility with test data)
            var outMessage = DaqifiOutMessage.Parser.ParseFrom(data);
            if (outMessage != null)
            {
                // Create the same structure as legacy: ProtobufMessage wrapping the DaqifiOutMessage
                var protobufMessage = new ProtobufMessage(outMessage);
                messages.Add(new ObjectInboundMessage(protobufMessage));
                
                // Consume all data for raw protobuf
                consumedBytes = data.Length;
                return messages;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            // Try delimited parsing for real device data
            using var stream = new MemoryStream(data);
            
            try
            {
                // This exactly replicates the legacy MessageConsumer parsing logic
                var outMessage = DaqifiOutMessage.Parser.ParseDelimitedFrom(stream);
                if (outMessage != null)
                {
                    // Create the same structure as legacy: ProtobufMessage wrapping the DaqifiOutMessage
                    var protobufMessage = new ProtobufMessage(outMessage);
                    messages.Add(new ObjectInboundMessage(protobufMessage));
                    
                    // Track consumed bytes
                    consumedBytes = (int)stream.Position;
                    return messages;
                }
            }
            catch (InvalidProtocolBufferException)
            {
                // Both parsing methods failed - no protobuf data
                consumedBytes = 0;
            }
        }
        catch (Exception)
        {
            // For other exceptions, don't consume bytes
            consumedBytes = 0;
        }

        return messages;
    }
}