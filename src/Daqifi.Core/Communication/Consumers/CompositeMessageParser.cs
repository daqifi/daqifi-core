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
    /// Parses raw data by intelligently trying both text and protobuf parsers.
    /// Uses heuristics beyond simple null byte detection to determine message type.
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

        // Use improved heuristics to detect message type
        var messageTypeHint = DetectMessageType(data);

        if (messageTypeHint == MessageTypeHint.LikelyProtobuf)
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

        if (messageTypeHint == MessageTypeHint.LikelyText)
        {
            // Try text parser first
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
        }

        // If heuristics are uncertain or first attempt failed, try the other parser
        if (messageTypeHint != MessageTypeHint.LikelyText)
        {
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
        }

        if (messageTypeHint != MessageTypeHint.LikelyProtobuf)
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
    /// Message type hints for improved detection.
    /// </summary>
    private enum MessageTypeHint
    {
        Uncertain,
        LikelyText,
        LikelyProtobuf
    }

    /// <summary>
    /// Uses multiple heuristics to detect the likely message type.
    /// Goes beyond simple null byte detection to avoid false positives.
    /// </summary>
    /// <param name="data">The data to analyze.</param>
    /// <returns>A hint about the likely message type.</returns>
    private static MessageTypeHint DetectMessageType(byte[] data)
    {
        if (data.Length == 0)
            return MessageTypeHint.Uncertain;

        // Heuristic 1: Check for printable ASCII (common in SCPI) - prioritize this
        var printableRatio = data.Count(b => b >= 32 && b <= 126) / (double)data.Length;
        if (printableRatio > 0.8) // More than 80% printable ASCII
        {
            return MessageTypeHint.LikelyText;
        }

        // Heuristic 2: Check for common text patterns (SCPI commands)
        if (data.Length > 3 && IsLikelyTextCommand(data))
        {
            return MessageTypeHint.LikelyText;
        }

        // Heuristic 3: High ratio of null bytes suggests binary
        var nullByteRatio = data.Count(b => b == 0) / (double)data.Length;
        if (nullByteRatio > 0.1) // More than 10% null bytes
        {
            return MessageTypeHint.LikelyProtobuf;
        }

        // Heuristic 4: Check for protobuf-like patterns (be more conservative)
        if (nullByteRatio > 0.05 && IsLikelyProtobufData(data)) // Only if some null bytes present
        {
            return MessageTypeHint.LikelyProtobuf;
        }

        return MessageTypeHint.Uncertain;
    }

    /// <summary>
    /// Checks if the data looks like a text command (SCPI-style).
    /// </summary>
    /// <param name="data">The data to check.</param>
    /// <returns>True if it looks like a text command.</returns>
    private static bool IsLikelyTextCommand(byte[] data)
    {
        // Check for common SCPI patterns
        var text = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 10));
        var fullText = System.Text.Encoding.ASCII.GetString(data);
        
        return text.StartsWith("*") || text.StartsWith("SYST") || text.StartsWith("CONF") || 
               text.StartsWith("READ", StringComparison.OrdinalIgnoreCase) ||
               fullText.EndsWith("\r\n") || fullText.EndsWith("\n");
    }

    /// <summary>
    /// Checks if the data has protobuf-like characteristics.
    /// </summary>
    /// <param name="data">The data to check.</param>
    /// <returns>True if it looks like protobuf data.</returns>
    private static bool IsLikelyProtobufData(byte[] data)
    {
        if (data.Length < 2)
            return false;

        // Protobuf messages often start with field tags (varint encoded)
        // Check for patterns that suggest protobuf field encoding
        for (int i = 0; i < Math.Min(data.Length - 1, 5); i++)
        {
            var byte1 = data[i];
            var byte2 = data[i + 1];
            
            // Look for varint patterns (field number + wire type)
            if ((byte1 & 0x07) <= 5 && // Valid wire type (0-5)
                (byte1 >> 3) > 0)      // Non-zero field number
            {
                return true;
            }
        }

        return false;
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