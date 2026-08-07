using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// A composite message parser that attempts to parse messages using multiple parsers.
/// This allows handling different message formats (text-based SCPI and binary protobuf) in the same stream.
/// </summary>
/// <remarks>
/// Classification is content-based and therefore approximate: it weighs how much of a
/// buffer is printable ASCII against how much of it is null bytes, and only consults
/// surface features such as a leading SCPI marker once those ratios have abstained. A
/// line ending is framing rather than content and is excluded from the printable ratio,
/// so it neither argues for text nor against it. Buffers that satisfy neither ratio —
/// real single-channel low-rate streaming frames contain no null bytes and no printable
/// ASCII at all — are handed to the protobuf parser first and fall back to the text
/// parser if it finds nothing. Code that already knows which format a link carries should
/// use <see cref="LineBasedMessageParser"/> or <see cref="ProtobufMessageParser"/>
/// directly rather than paying for the guess.
/// </remarks>
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

        // Whichever parser the heuristics favour runs first; the other is the fallback
        // for when that parser finds nothing. Routing the wrong way is not merely slow:
        // LineBasedMessageParser will happily swallow an entire binary frame as one
        // "line" and report it consumed, destroying the frame for good.
        //
        // Uncertain buffers try protobuf first. Uncertain means the buffer failed the
        // printable-ratio test, so it is not plausibly SCPI text — and some real
        // streaming shapes leave every ratio heuristic abstaining: a single analog
        // channel at 10 Hz produces 10-byte frames containing neither a null byte nor
        // a printable ASCII byte (measured on an Nq1 running fw 3.7.2, issue #268).
        if (messageTypeHint == MessageTypeHint.LikelyText)
        {
            if (TryParse(_textParser, data, messages, ref consumedBytes))
                return messages;

            TryParse(_protobufParser, data, messages, ref consumedBytes);
        }
        else
        {
            if (TryParse(_protobufParser, data, messages, ref consumedBytes))
                return messages;

            TryParse(_textParser, data, messages, ref consumedBytes);
        }

        return messages;
    }

    /// <summary>
    /// Runs <paramref name="parser"/> over <paramref name="data"/> and appends whatever it
    /// produced to <paramref name="messages"/>, reporting whether anything was parsed.
    /// <paramref name="consumedBytes"/> is only updated when the parser produced at least
    /// one message, so a parser that consumed bytes while finding nothing (the protobuf
    /// parser resyncing over garbage, for instance) never hides those bytes from the
    /// fallback parser.
    /// </summary>
    private static bool TryParse<T>(
        IMessageParser<T> parser,
        byte[] data,
        List<IInboundMessage<object>> messages,
        ref int consumedBytes)
    {
        var parsed = parser.ParseMessages(data, out var parserConsumed);

        var parsedAny = false;
        foreach (var msg in parsed)
        {
            messages.Add(new ObjectInboundMessage(msg.Data!));
            parsedAny = true;
        }

        if (parsedAny)
        {
            consumedBytes = parserConsumed;
        }

        return parsedAny;
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
    /// Minimum fraction of printable ASCII, measured over content bytes only, for a
    /// buffer to be taken as text outright. See <see cref="MeasureRatios"/>.
    /// </summary>
    private const double TextPrintableRatio = 0.8;

    /// <summary>
    /// Minimum fraction of printable ASCII for a leading SCPI marker to count as
    /// evidence of text. A marker on a buffer that is mostly non-printable is far
    /// more likely to be an echoed command in front of a binary reply than a text
    /// message.
    /// </summary>
    private const double MarkerPrintableRatio = 0.5;

    /// <summary>
    /// Fraction of null bytes above which a buffer is taken as binary.
    /// </summary>
    private const double BinaryNullRatio = 0.1;

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

        var (printableRatio, nullByteRatio) = MeasureRatios(data);

        // Heuristic 1: Check for printable ASCII (common in SCPI) - prioritize this
        if (printableRatio > TextPrintableRatio) // More than 80% printable ASCII
        {
            return MessageTypeHint.LikelyText;
        }

        // Heuristic 2: High ratio of null bytes suggests binary.
        //
        // This must be tested before any surface-level text marker (heuristic 3).
        // A DAQiFi device terminates its BINARY SYSTem:SYSInfoPB? reply with an
        // ASCII CRLF, and with echo on it also prefixes the reply with the echoed
        // command text — so both a leading "SYST" and a trailing CRLF appear on a
        // buffer that is 40% null bytes. Measured on an Nq1 running fw 3.7.2, the
        // real 588-byte status frame is 24% printable and 40.5% null (issue #268).
        // Null density is the trustworthy signal; a text-shaped edge is not.
        if (nullByteRatio > BinaryNullRatio) // More than 10% null bytes
        {
            return MessageTypeHint.LikelyProtobuf;
        }

        // Heuristic 3: Check for common text patterns (SCPI commands)
        if (data.Length > 3 && printableRatio > MarkerPrintableRatio && StartsWithScpiMarker(data))
        {
            return MessageTypeHint.LikelyText;
        }

        // Heuristic 4: Check for protobuf-like patterns (be more conservative)
        if (nullByteRatio > 0.05 && IsLikelyProtobufData(data)) // Only if some null bytes present
        {
            return MessageTypeHint.LikelyProtobuf;
        }

        return MessageTypeHint.Uncertain;
    }

    /// <summary>
    /// Measures the two signals the classifier weighs: what fraction of the buffer's
    /// content bytes are printable ASCII, and what fraction of the whole buffer is null
    /// bytes.
    /// </summary>
    /// <remarks>
    /// Line endings and tabs are excluded from the printable ratio entirely — they
    /// neither raise nor lower it. In a line-oriented text protocol a line ending is
    /// framing, not content, so counting it against printability misjudges exactly the
    /// replies that are shortest: "0\r\n" (the reply to SYSTem:STReam:FORmat?) is 33%
    /// printable by raw count and 100% printable by content. Getting those wrong matters,
    /// because a text buffer that fails this test is handed to the protobuf parser first,
    /// where a chance parse would consume the reply and destroy it.
    ///
    /// Null bytes are measured over the whole buffer, since a null is never framing.
    /// </remarks>
    private static (double PrintableRatio, double NullByteRatio) MeasureRatios(byte[] data)
    {
        var contentBytes = 0;
        var printableBytes = 0;
        var nullBytes = 0;

        foreach (var b in data)
        {
            if (b == 0)
            {
                nullBytes++;
            }

            if (b is 0x0D or 0x0A or 0x09) // CR, LF, TAB: framing, not content
            {
                continue;
            }

            contentBytes++;
            if (b >= 32 && b <= 126)
            {
                printableBytes++;
            }
        }

        // An all-line-ending buffer has no content to judge; report it as non-printable
        // and let the remaining heuristics (or Uncertain) decide.
        var printableRatio = contentBytes == 0 ? 0.0 : printableBytes / (double)contentBytes;
        return (printableRatio, nullBytes / (double)data.Length);
    }

    /// <summary>
    /// Checks whether the data opens with a marker characteristic of SCPI text.
    /// </summary>
    /// <param name="data">The data to check.</param>
    /// <returns>True if it looks like a text command.</returns>
    /// <remarks>
    /// Deliberately looks only at the head of the buffer. An earlier version also
    /// accepted any buffer ending in a line ending, which misrouted the binary
    /// SYSTem:SYSInfoPB? reply — the device terminates that frame with an ASCII
    /// CRLF — into <see cref="LineBasedMessageParser"/>, where the whole frame was
    /// consumed as a single garbage line and the protobuf message was lost. A
    /// trailing CRLF says nothing about a buffer that is 40% null bytes; the
    /// printable- and null-ratio heuristics already classify those correctly.
    /// </remarks>
    private static bool StartsWithScpiMarker(byte[] data)
    {
        var head = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 10));

        return head.StartsWith("*", StringComparison.Ordinal) ||
               head.StartsWith("SYST", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("CONF", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("READ", StringComparison.OrdinalIgnoreCase);
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