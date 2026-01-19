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
    private const int MaxVarint32Bytes = 5;
    
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
            var remainingData = new ReadOnlySpan<byte>(data, currentIndex, data.Length - currentIndex);

            if (!TryReadLengthPrefix(remainingData, out var messageLength, out var prefixBytes))
            {
                break; // Not enough data for length prefix yet.
            }

            if (messageLength <= 0)
            {
                currentIndex += Math.Max(prefixBytes, 1);
                consumedBytes = currentIndex;
                retryCount++;
                continue;
            }

            if (remainingData.Length < prefixBytes + messageLength)
            {
                break; // Wait for more data.
            }

            try
            {
                var payload = remainingData.Slice(prefixBytes, messageLength).ToArray();
                var message = DaqifiOutMessage.Parser.ParseFrom(payload);
                currentIndex += prefixBytes + messageLength;
                consumedBytes = currentIndex;
                messages.Add(new ProtobufMessage(message));
                retryCount = 0;
            }
            catch (InvalidProtocolBufferException)
            {
                currentIndex++;
                consumedBytes = currentIndex;
                retryCount++;
            }
            catch (Exception)
            {
                currentIndex++;
                consumedBytes = currentIndex;
                retryCount++;
            }
        }

        return messages;
    }

    private static bool TryReadLengthPrefix(ReadOnlySpan<byte> data, out int length, out int bytesRead)
    {
        length = 0;
        bytesRead = 0;
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

        return false;
    }
}
