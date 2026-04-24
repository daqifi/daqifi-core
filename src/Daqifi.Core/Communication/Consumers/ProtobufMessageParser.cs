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

    // Upper bound on a single DaqifiOutMessage frame. Real streaming frames are well
    // under 1 KB even at 32 channels (16 analog floats + 16 digital bits + headers);
    // initial-status frames that include channel metadata and calibration are a few
    // KB at most. 4 KB leaves generous headroom for any legitimate frame while
    // cutting off the failure mode where boot-time garbage on the serial port
    // happens to form a plausible varint prefix encoding tens of KB — previously
    // accepted under the old 1 MB cap, which left the parser waiting indefinitely
    // for data that would never arrive (device LED streaming, but zero frames
    // parsed; the buffer grows unbounded). Declared lengths over this cap are
    // rejected immediately and the parser resyncs one byte at a time.
    private const int MaxMessageSizeBytes = 4 * 1024;

    // Maximum gap between the declared message length and the bytes currently
    // available before we give up waiting and treat the prefix as garbage.
    // A legitimate partial frame is always small — the stream consumer feeds
    // ParseMessages every few milliseconds and real frames are <1 KB, so a real
    // partial never leaves a multi-KB gap between declared and available bytes.
    // A prefix that *claims* thousands of bytes and is nowhere close to being
    // satisfied is overwhelmingly likely to be boot-time garbage (USB CDC setup,
    // DTR pulse bytes) that happens to varint-encode a large value. Capping the
    // tolerable gap bounds the worst-case stall: without this, a plausible-under-
    // the-cap bogus prefix still deadlocks until the buffer reaches the declared
    // size, which at slow sample rates can be many seconds.
    private const int MaxPartialFrameGapBytes = 1024;

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
                // Not enough buffered data for this frame yet. For a real partial frame
                // this just means "wait a bit" — streaming messages are well under 1 KB,
                // so the next read or two will complete it. But a plausible-looking yet
                // bogus prefix can claim thousands of bytes, and waiting for that much
                // never-arriving data is exactly the stall that masqueraded as "parser
                // hung": device LED blinking, buffer growing, zero messages parsed.
                //
                // Two recovery gates protect the wait path from garbage:
                //
                //  1. Gap gate: a declared length more than MaxPartialFrameGapBytes
                //     beyond what's currently buffered is almost certainly garbage.
                //     Real partials for streaming frames leave gaps measured in
                //     hundreds of bytes, not kilobytes.
                //
                //  2. Resync gate: if we've already advanced past bytes in this call
                //     but haven't yet produced a message, we're actively resyncing
                //     past garbage. Stopping on the first plausible-but-unsatisfied
                //     prefix would re-enter the same garbage on every subsequent
                //     call and never make progress. Keep advancing one byte at a
                //     time until we either parse something or run out of buffer.
                //     (If we'd already parsed a real message earlier in the call,
                //     messages.Count > 0 and we take the break path — that's a
                //     genuine partial for the next frame, not a resync.)
                if (messageLength - remainingData.Length > MaxPartialFrameGapBytes
                    || (consumedBytes > 0 && messages.Count == 0))
                {
                    currentIndex++;
                    consumedBytes = currentIndex;
                    continue;
                }

                break;
            }

            try
            {
                // ParseFrom(ReadOnlySpan) avoids the per-attempt byte[] copy, which matters
                // because byte-by-byte resync over a garbage buffer can call this many times
                // before finding (or failing to find) a valid frame.
                var message = DaqifiOutMessage.Parser.ParseFrom(remainingData.Slice(prefixBytes, messageLength));
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
