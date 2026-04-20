using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Message parser for binary protobuf messages.
/// Uses CodedInputStream to properly track consumed bytes and handle variable-length messages.
/// </summary>
public class ProtobufMessageParser : IMessageParser<DaqifiOutMessage>
{
    private const int MaxVarint32Bytes = 5;
    private const int MaxMessageSizeBytes = 1024 * 1024;

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
            var remainingData = new ReadOnlySpan<byte>(data, currentIndex, data.Length - currentIndex);

            if (!TryReadLengthPrefix(remainingData, out var messageLength, out var prefixBytes, out var prefixIsMalformed))
            {
                if (prefixIsMalformed)
                {
                    // Skip this byte and resync; advancing by 1 (not by prefixBytes) is
                    // important because the malformed run may overlap a valid frame that
                    // starts mid-way through it.
                    currentIndex++;
                    consumedBytes = currentIndex;
                    continue;
                }

                break; // Not enough data for length prefix yet — wait for the next read.
            }

            if (messageLength <= 0 || messageLength > MaxMessageSizeBytes)
            {
                currentIndex++;
                consumedBytes = currentIndex;
                continue;
            }

            if (remainingData.Length < prefixBytes + messageLength)
            {
                // Not enough buffered data for this frame yet. It might be a real frame
                // waiting on more bytes — bail and wait. If the prefix turns out to be
                // garbage, the next read will append more data and we'll retry from the
                // same position; once a real frame eventually appears we resync to it
                // by advancing one byte at a time on parse failures above.
                break;
            }

            try
            {
                var payload = remainingData.Slice(prefixBytes, messageLength).ToArray();
                var message = DaqifiOutMessage.Parser.ParseFrom(payload);
                currentIndex += prefixBytes + messageLength;
                consumedBytes = currentIndex;
                messages.Add(new ProtobufMessage(message));
            }
            catch (Exception)
            {
                currentIndex++;
                consumedBytes = currentIndex;
            }
        }

        return messages;
    }

    private static bool TryReadLengthPrefix(
        ReadOnlySpan<byte> data,
        out int length,
        out int bytesRead,
        out bool isMalformed)
    {
        length = 0;
        bytesRead = 0;
        isMalformed = false;
        var shift = 0;

        for (var i = 0; i < MaxVarint32Bytes; i++)
        {
            if (i >= data.Length)
            {
                return false;
            }

            var value = data[i];
            length |= (value & 0x7F) << shift;
            bytesRead++;

            if ((value & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        isMalformed = true;
        return false;
    }
}
