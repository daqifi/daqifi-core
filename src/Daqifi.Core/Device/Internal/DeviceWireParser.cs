using System;
using System.Collections.Generic;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The parser behind the device's one reader: frames what arrives as protobuf, or — while a text
/// exchange has installed a <see cref="TextReplyCapture"/> — as reply lines (issue #900).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it switches rather than guesses.</b> <see cref="CompositeMessageParser"/> can also read
/// both, by classifying each buffer on its content. That classification is a guess, and on this
/// wire a wrong guess destroys data: the line parser swallows a binary frame as one "line", and the
/// protobuf parser resyncs over a text reply a byte at a time (#268 is the record of the binary
/// <c>SYSTem:SYSInfoPB?</c> reply being misread exactly that way). The exchange never needs to
/// guess — it knows it is about to ask for text — so it says so, and both framings stay exact.
/// </para>
/// <para>
/// <b>What it cannot do.</b> It frames one way at a time. A protobuf frame that arrives while an
/// exchange holds it in text mode is framed as text and lost to the stream, exactly as it was when a
/// separate text reader took the wire. So a text exchange still cannot run alongside a stream that
/// is delivering data without costing that stream its frames for the exchange's duration — which is
/// why <see cref="DaqifiStreamingDevice"/> still only reads the error queue on a start that has sent
/// nothing (#912).
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Capture"/> is only ever changed through
/// <see cref="StreamMessageConsumer{T}.Reframe"/>, which writes it under the same buffer lock the
/// reader holds while it parses. So a switch always falls between two parses, never inside one, and
/// the bytes left over from the old framing are discarded at the same instant — never reinterpreted
/// under the new one.
/// </para>
/// </remarks>
internal sealed class DeviceWireParser : IMessageParser<WireMessage>
{
    /// <summary>
    /// Frames reply lines exactly as the separate text reader did. Both settings exist because the
    /// exchange decides that the device has answered by counting lines (issue #538): splitting on a
    /// bare LF reads the firmware's LF-only replies as well as its CRLF ones, and emitting blank
    /// lines keeps the empty <c>SYSTem:LOG?</c> dump — a lone CRLF — visible to that count.
    /// </summary>
    private readonly LineBasedMessageParser _lineParser = new(lineEnding: "\n") { EmitEmptyLines = true };

    private TextReplyCapture? _capture;

    /// <summary>
    /// The exchange the reader is framing lines for, or <c>null</c> when it frames protobuf.
    /// </summary>
    /// <remarks>
    /// Set only from a <see cref="StreamMessageConsumer{T}.Reframe"/> callback — see the threading
    /// note on the class.
    /// </remarks>
    internal TextReplyCapture? Capture
    {
        get => _capture;
        set => _capture = value;
    }

    /// <inheritdoc />
    public IEnumerable<IInboundMessage<WireMessage>> ParseMessages(byte[] data, out int consumedBytes)
        => ParseMessages(new ReadOnlySpan<byte>(data), out consumedBytes);

    /// <inheritdoc />
    /// <remarks>
    /// Overrides the span entry point so the reader never copies its accumulation buffer (#490).
    /// </remarks>
    public IEnumerable<IInboundMessage<WireMessage>> ParseMessages(ReadOnlySpan<byte> data, out int consumedBytes)
    {
        var capture = _capture;
        return capture == null
            ? ParseFrames(data, out consumedBytes)
            : ParseLines(data, capture, out consumedBytes);
    }

    /// <summary>
    /// Frames protobuf exactly as <see cref="ProtobufMessageParser"/> does — the same frame step,
    /// resync rules included — and wraps each frame once.
    /// </summary>
    private static List<IInboundMessage<WireMessage>> ParseFrames(ReadOnlySpan<byte> data, out int consumedBytes)
    {
        var messages = new List<IInboundMessage<WireMessage>>();
        consumedBytes = 0;

        var offset = 0;
        while (true)
        {
            var step = ProtobufMessageParser.StepFrame(
                data,
                offset,
                anyFrameRead: messages.Count > 0,
                out var frame,
                out var nextOffset);
            if (step == ProtobufFrameStep.EndOfBuffer)
            {
                break;
            }

            offset = nextOffset;
            consumedBytes = offset;

            if (step == ProtobufFrameStep.Frame)
            {
                messages.Add(WireMessage.ForFrame(frame!));
            }
        }

        return messages;
    }

    private List<IInboundMessage<WireMessage>> ParseLines(
        ReadOnlySpan<byte> data,
        TextReplyCapture capture,
        out int consumedBytes)
    {
        var messages = new List<IInboundMessage<WireMessage>>();
        foreach (var line in _lineParser.ParseMessages(data, out consumedBytes))
        {
            messages.Add(WireMessage.ForLine(line.Data, capture));
        }

        return messages;
    }
}
