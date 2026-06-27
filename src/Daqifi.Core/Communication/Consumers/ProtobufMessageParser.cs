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
                // Two recovery gates protect the wait path from garbage while leaving
                // real partial frames intact for the caller to complete:
                //
                //  1. Gap gate: a declared length more than MaxPartialFrameGapBytes
                //     beyond the *payload* bytes currently buffered (remainingData
                //     minus the prefix we already consumed when reading it) is almost
                //     certainly garbage. Real partials for streaming frames leave
                //     gaps measured in hundreds of bytes, not kilobytes.
                //
                //  2. Field-tag gate: when at least one body byte is available, we
                //     can peek at it. The first body byte of a real DaqifiOutMessage
                //     is a protobuf field tag — field_number >= 1 and wire_type in
                //     {0,1,2,5}. A byte that fails both conditions (e.g., wire_type
                //     of deprecated SGROUP/EGROUP or field_number == 0) is not a
                //     real frame start; treat it as garbage. A multi-byte tag
                //     (continuation bit set) can't be fully validated from one byte,
                //     so we let it through and wait.
                //
                // Neither gate advances into a real frame's prefix or payload: only
                // into bytes already identified as implausible. Callers that trim
                // consumedBytes from their buffer never lose recoverable data.
                var availableBodyBytes = remainingData.Length - prefixBytes;
                var missingPayload = messageLength - availableBodyBytes;
                var firstBodyByteIsGarbage = availableBodyBytes > 0
                    && !IsPlausibleFieldTagByte(remainingData[prefixBytes]);

                // Gap gate ONLY applies in the pure-prefix-no-body case
                // (no body bytes arrived). Once any body byte is buffered,
                // the field-tag gate above provides positive evidence that
                // the prefix really is a frame start; a large declared
                // length at that point is just a multi-chunk read of a
                // legitimate frame (e.g. a multi-KB initial-status frame
                // on a transport that returns smaller reads). Including
                // availableBodyBytes == 1 here would corrupt real frames
                // whose first body byte arrives alone — a single-byte
                // mistaken advance is unrecoverable because callers trim
                // consumedBytes from their buffer.
                var gapIsSuspicious = availableBodyBytes == 0
                    && missingPayload > MaxPartialFrameGapBytes;

                if (gapIsSuspicious || firstBodyByteIsGarbage)
                {
                    currentIndex++;
                    consumedBytes = currentIndex;
                    continue;
                }

                // The declared frame isn't fully buffered. Normally that means a
                // genuine frame split across reads — wait for the rest. But the
                // same condition is reached by leading non-protobuf noise whose
                // bytes happen to varint-decode to a plausible length with a
                // plausible first body byte. The motivating case (issue #268): an
                // echo-on device prefixes its SYSInfoPB? reply with the echoed
                // ASCII command ("SYSTem:SYSInfoPB?\r\n") and trails it with a
                // "DAQIFI>" prompt. The leading 'S' (0x53) decodes as length 83
                // and the next byte 'Y' looks like a field tag, so a plain wait
                // would block forever on 83 bytes that never arrive while the real
                // frame sitting further along is never parsed — the device is
                // silently missed.
                //
                // Tell the two apart structurally: a genuine partial frame runs
                // past the end of the buffer, so nothing parseable can follow it;
                // noise, by contrast, is followed by the real, complete frame. Scan
                // ahead for the next fully-buffered, parseable frame. If one exists,
                // the current position is noise — skip straight to it (everything
                // before the first parseable frame is, by definition, not a frame
                // start and holds no recoverable partial, since a partial would
                // extend past the buffer end and thus past that frame). If none
                // exists, this really is a partial: preserve it and wait.
                //
                // Only do this when no frame has been parsed yet in this call. Once
                // we've emitted at least one frame from this buffer, a trailing
                // under-buffered frame is overwhelmingly a genuine split frame to
                // wait for (the normal streaming case) — not leading noise — so we
                // skip the linear scan and keep that hot path cheap. Stray bytes
                // appearing mid-stream are still recovered: the caller trims the
                // preceding frame and those bytes lead the next call, where no frame
                // has been parsed yet and the scan runs. Discovery always reaches
                // here with zero frames parsed, so it recovers on the first reply.
                if (messages.Count == 0)
                {
                    var nextFrameIndex = FindNextParseableFrameStart(data, currentIndex + 1);
                    if (nextFrameIndex >= 0)
                    {
                        currentIndex = nextFrameIndex;
                        consumedBytes = currentIndex;
                        continue;
                    }
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

    /// <summary>
    /// Scans forward from <paramref name="start"/> for the first index at which a
    /// complete, fully-buffered, successfully-parseable length-delimited frame
    /// begins, returning that index or -1 if none exists before the end of
    /// <paramref name="data"/>. Used by the partial-frame wait path to tell leading
    /// non-protobuf noise (echoed command text, a "DAQIFI>" prompt, stray bytes)
    /// apart from a real frame split across reads: noise is followed by a parseable
    /// frame, a genuine partial is not.
    /// </summary>
    /// <remarks>
    /// The scan only recognizes frames whose declared length is fully present and
    /// whose body parses — it never reports a partial and never emits a message, so
    /// it cannot advance into (and corrupt) a real frame that is still arriving.
    /// It is a bounded linear scan, and the caller only invokes it when no frame has
    /// been parsed from the buffer yet (leading-noise recovery), so the normal
    /// streaming path — which parses frames before reaching a trailing partial —
    /// never pays for it.
    /// </remarks>
    private static int FindNextParseableFrameStart(byte[] data, int start)
    {
        for (var i = start; i < data.Length; i++)
        {
            var remaining = new ReadOnlySpan<byte>(data, i, data.Length - i);

            if (!TryReadLengthPrefix(remaining, out var messageLength, out var prefixBytes, out _))
            {
                // Incomplete or malformed length prefix here — not a frame start.
                continue;
            }

            if (messageLength <= 0 || messageLength > MaxMessageSizeBytes)
            {
                continue;
            }

            if (remaining.Length < prefixBytes + messageLength)
            {
                // Declared frame isn't fully buffered at i, so it can't be the
                // complete, parseable frame we're scanning for.
                continue;
            }

            try
            {
                DaqifiOutMessage.Parser.ParseFrom(remaining.Slice(prefixBytes, messageLength));
                return i;
            }
            catch (Exception)
            {
                // A varint length that happens to fit but whose body isn't a valid
                // message is not a real frame boundary — keep scanning.
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns true if <paramref name="b"/> could plausibly be the first byte of a
    /// real DaqifiOutMessage body (a protobuf field tag). Used by the partial-frame
    /// wait path to reject garbage whose varint prefix happens to decode to a
    /// plausible-looking length. Multi-byte tags (continuation bit set) can't have
    /// their full field number validated from a single byte, but the wire type
    /// always lives in the low 3 bits of the first byte regardless — so impossible
    /// wire types are rejected even on continuation bytes.
    /// </summary>
    private static bool IsPlausibleFieldTagByte(byte b)
    {
        // Wire type lives in the low 3 bits regardless of whether the field
        // number spans multiple bytes. Valid wire types are VARINT(0), I64(1),
        // LEN(2), I32(5). Wire types 3 (SGROUP) and 4 (EGROUP) are deprecated
        // group types that DaqifiOutMessage does not use; 6/7 are undefined.
        var wireType = b & 0x07;
        if (wireType is 3 or 4 or 6 or 7)
        {
            return false;
        }

        // Continuation bit set → multi-byte tag. The field number spans more
        // bytes which we may not have yet; accept once wire type passed.
        if ((b & 0x80) != 0)
        {
            return true;
        }

        // Single-byte tag: bits 6..3 are the field number. Field number 0 is
        // reserved and never appears in a real message.
        var fieldNumber = (b >> 3) & 0x0F;
        return fieldNumber != 0;
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
