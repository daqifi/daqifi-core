using Daqifi.Core.Communication.Messages;
using System.Text;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Message parser for line-based text protocols (like SCPI responses).
/// Splits messages on line endings and creates text-based inbound messages.
/// </summary>
public class LineBasedMessageParser : IMessageParser<string>
{
    private readonly byte[] _lineEnding;
    private readonly Encoding _encoding;

    /// <summary>
    /// Initializes a new instance of the LineBasedMessageParser class.
    /// </summary>
    /// <param name="lineEnding">
    /// The line ending to split messages on. Defaults to CRLF. Must encode to at least one byte.
    /// Passing a bare <c>"\n"</c> reads LF-terminated and CRLF-terminated data alike, because the
    /// carriage return is left at the end of the line and trimmed off with the rest of the trailing
    /// whitespace — which is what a protocol with both kinds of reply on the same wire needs.
    /// </param>
    /// <param name="encoding">The text encoding to use. Defaults to UTF-8.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lineEnding"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lineEnding"/> encodes to zero bytes (an empty line ending would prevent the parser from making progress).</exception>
    public LineBasedMessageParser(string lineEnding = "\r\n", Encoding? encoding = null)
    {
        if (lineEnding == null)
            throw new ArgumentNullException(nameof(lineEnding));

        _encoding = encoding ?? Encoding.UTF8;
        _lineEnding = _encoding.GetBytes(lineEnding);

        if (_lineEnding.Length == 0)
            throw new ArgumentException(
                "Line ending must encode to at least one byte; an empty line ending would prevent the parser from making progress.",
                nameof(lineEnding));
    }

    /// <summary>
    /// Whether a complete line whose content is empty (or entirely whitespace) is emitted as a
    /// message with empty <see cref="IInboundMessage{T}.Data"/>, instead of being dropped.
    /// Defaults to <c>false</c>, which is the historical behaviour.
    /// </summary>
    /// <remarks>
    /// Internal because it exists for one caller: the SCPI text exchange, which decides that the
    /// device has answered by counting the messages this parser produces. A blank line is still an
    /// answer — the DAQiFi firmware terminates its <c>SYSTem:LOG?</c> dump with one, so an empty log
    /// arrives as a lone CRLF and nothing else. Dropping it here made the exchange conclude that
    /// nothing had arrived and wait out its full first-response timeout (issue #538). The exchange
    /// filters the blanks back out of the result, so its callers see the same lines as before.
    /// </remarks>
    internal bool EmitEmptyLines { get; init; }

    /// <summary>
    /// Parses raw data into line-based text messages.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed text messages.</returns>
    public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
        => ParseMessages(new ReadOnlySpan<byte>(data), out consumedBytes);

    /// <summary>
    /// Parses raw data into line-based text messages directly from the caller's buffer.
    /// </summary>
    /// <remarks>
    /// Overriding the span entry point keeps <see cref="StreamMessageConsumer{T}"/> from copying
    /// its accumulation buffer out on every read (issue #490), and decodes each line straight from
    /// the buffer rather than through a per-line intermediate array. Nothing here retains
    /// <paramref name="data"/> past the call.
    /// </remarks>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed text messages.</returns>
    public IEnumerable<IInboundMessage<string>> ParseMessages(ReadOnlySpan<byte> data, out int consumedBytes)
    {
        var messages = new List<IInboundMessage<string>>();
        consumedBytes = 0;

        if (data.Length == 0)
            return messages;

        var searchStart = 0;
        while (searchStart < data.Length)
        {
            // Find the next line ending
            var lineEndIndex = FindLineEnding(data, searchStart);
            if (lineEndIndex == -1)
            {
                // No complete line found, stop parsing
                break;
            }

            // Extract the line (excluding line ending). Trimming here is what lets a parser
            // configured with a bare "\n" read CRLF data unchanged: the CR lands at the end of the
            // slice and comes straight back off. Do not remove it without replacing it.
            var lineLength = lineEndIndex - searchStart;
            var messageText = lineLength > 0
                ? _encoding.GetString(data.Slice(searchStart, lineLength)).Trim()
                : string.Empty;

            if (messageText.Length > 0 || EmitEmptyLines)
            {
                messages.Add(new TextInboundMessage(messageText));
            }

            // Move past this line and its ending
            searchStart = lineEndIndex + _lineEnding.Length;
            consumedBytes = searchStart;
        }

        return messages;
    }

    /// <summary>
    /// Finds the index of the next line ending in the data.
    /// </summary>
    /// <param name="data">The data to search.</param>
    /// <param name="startIndex">The index to start searching from.</param>
    /// <returns>The index of the line ending, or -1 if not found.</returns>
    private int FindLineEnding(ReadOnlySpan<byte> data, int startIndex)
    {
        for (int i = startIndex; i <= data.Length - _lineEnding.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < _lineEnding.Length; j++)
            {
                if (data[i + j] != _lineEnding[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}

/// <summary>
/// Simple implementation of IInboundMessage for text data.
/// </summary>
public class TextInboundMessage : IInboundMessage<string>
{
    /// <summary>
    /// Initializes a new instance of the TextInboundMessage class.
    /// </summary>
    /// <param name="data">The text data of the message.</param>
    public TextInboundMessage(string data)
    {
        Data = data;
    }

    /// <summary>
    /// Gets the text data of the message.
    /// </summary>
    public string Data { get; }
}