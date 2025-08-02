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
    /// <param name="lineEnding">The line ending to split messages on. Defaults to CRLF.</param>
    /// <param name="encoding">The text encoding to use. Defaults to UTF-8.</param>
    public LineBasedMessageParser(string lineEnding = "\r\n", Encoding? encoding = null)
    {
        _encoding = encoding ?? Encoding.UTF8;
        _lineEnding = _encoding.GetBytes(lineEnding);
    }

    /// <summary>
    /// Parses raw data into line-based text messages.
    /// </summary>
    /// <param name="data">The raw data to parse.</param>
    /// <param name="consumedBytes">The number of bytes consumed from the data during parsing.</param>
    /// <returns>A collection of parsed text messages.</returns>
    public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
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

            // Extract the line (excluding line ending)
            var lineLength = lineEndIndex - searchStart;
            if (lineLength > 0)
            {
                var lineData = new byte[lineLength];
                Array.Copy(data, searchStart, lineData, 0, lineLength);
                
                var messageText = _encoding.GetString(lineData);
                if (!string.IsNullOrWhiteSpace(messageText))
                {
                    messages.Add(new TextInboundMessage(messageText.Trim()));
                }
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
    private int FindLineEnding(byte[] data, int startIndex)
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