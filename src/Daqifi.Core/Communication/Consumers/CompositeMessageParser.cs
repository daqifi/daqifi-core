using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// A composite message parser that attempts to parse messages using multiple parsers.
/// This allows handling different message formats (text-based SCPI and binary protobuf) in the same stream.
/// </summary>
public class CompositeMessageParser : IMessageParser<object>
{
    private readonly IMessageParser<string> _textParser;
    private readonly IMessageParser<DaqifiOutMessage> _protobufParser;

    /// <summary>
    /// Initializes a new instance of the CompositeMessageParser class.
    /// </summary>
    /// <param name="textParser">Parser for text-based messages (e.g., SCPI responses).</param>
    /// <param name="protobufParser">Parser for binary protobuf messages.</param>
    public CompositeMessageParser(
        IMessageParser<string>? textParser = null, 
        IMessageParser<DaqifiOutMessage>? protobufParser = null)
    {
        _textParser = textParser ?? new LineBasedMessageParser();
        _protobufParser = protobufParser ?? new ProtobufMessageParser();
    }

    /// <summary>
    /// Parses raw data by trying both text and protobuf parsers.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed messages of various types.</returns>
    public IEnumerable<IInboundMessage<object>> ParseMessages(byte[] data, out int consumedBytes)
    {
        var messages = new List<IInboundMessage<object>>();
        consumedBytes = 0;

        if (data.Length == 0)
            return messages;

        // First, try to detect if this looks like protobuf data (contains null bytes)
        bool likelyProtobuf = ContainsNullBytes(data);

        if (likelyProtobuf)
        {
            // Try protobuf parser first
            var protobufMessages = _protobufParser.ParseMessages(data, out int protobufConsumed);
            if (protobufMessages.Any())
            {
                foreach (var msg in protobufMessages)
                {
                    messages.Add(new ObjectInboundMessage(msg.Data));
                }
                consumedBytes = protobufConsumed;
                return messages;
            }
        }

        // Try text parser
        var textMessages = _textParser.ParseMessages(data, out int textConsumed);
        if (textMessages.Any())
        {
            foreach (var msg in textMessages)
            {
                messages.Add(new ObjectInboundMessage(msg.Data));
            }
            consumedBytes = textConsumed;
            return messages;
        }

        // If text parser didn't work and we haven't tried protobuf yet, try it
        if (!likelyProtobuf)
        {
            var protobufMessages = _protobufParser.ParseMessages(data, out int protobufConsumed);
            if (protobufMessages.Any())
            {
                foreach (var msg in protobufMessages)
                {
                    messages.Add(new ObjectInboundMessage(msg.Data));
                }
                consumedBytes = protobufConsumed;
            }
        }

        return messages;
    }

    /// <summary>
    /// Checks if the data contains null bytes, which indicates binary protobuf data.
    /// </summary>
    /// <param name="data">The data to check.</param>
    /// <returns>True if the data contains null bytes, false otherwise.</returns>
    private static bool ContainsNullBytes(byte[] data)
    {
        return data.Contains((byte)0);
    }
}

/// <summary>
/// Simple implementation of IInboundMessage for generic object data.
/// </summary>
public class ObjectInboundMessage : IInboundMessage<object>
{
    /// <summary>
    /// Initializes a new instance of the ObjectInboundMessage class.
    /// </summary>
    /// <param name="data">The object data of the message.</param>
    public ObjectInboundMessage(object data)
    {
        Data = data;
    }

    /// <summary>
    /// Gets the object data of the message.
    /// </summary>
    public object Data { get; }
}