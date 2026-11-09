using System;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// One thing the device's reader took off the wire: a protobuf frame, or — while a text exchange
/// holds the reader in text mode — one reply line, tagged with the exchange it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The device used to run two readers, one per kind, and swap them around every text exchange
/// (issue #900). One reader that frames both needs one message type that can be either, and this
/// is it. Exactly one of <see cref="Frame"/> and <see cref="Line"/> is set.
/// </para>
/// <para>
/// It is its own <see cref="IInboundMessage{T}"/>, so a protobuf frame costs one allocation on the
/// streaming path — the same one <see cref="ProtobufMessage"/> cost before — rather than a wrapper
/// around a wrapper.
/// </para>
/// </remarks>
internal sealed class WireMessage : IInboundMessage<WireMessage>
{
    private WireMessage(DaqifiOutMessage? frame, string? line, TextReplyCapture? capture)
    {
        Frame = frame;
        Line = line;
        Capture = capture;
    }

    /// <summary>
    /// Wraps a decoded protobuf frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The message.</returns>
    internal static WireMessage ForFrame(DaqifiOutMessage frame) =>
        new(frame ?? throw new ArgumentNullException(nameof(frame)), line: null, capture: null);

    /// <summary>
    /// Wraps one reply line framed while <paramref name="capture"/> held the reader.
    /// </summary>
    /// <param name="line">The line, trimmed, possibly empty.</param>
    /// <param name="capture">The exchange the line was framed for.</param>
    /// <returns>The message.</returns>
    internal static WireMessage ForLine(string line, TextReplyCapture capture) =>
        new(
            frame: null,
            line ?? throw new ArgumentNullException(nameof(line)),
            capture ?? throw new ArgumentNullException(nameof(capture)));

    /// <summary>
    /// The protobuf frame, or <c>null</c> when this is a reply line.
    /// </summary>
    internal DaqifiOutMessage? Frame { get; }

    /// <summary>
    /// The reply line, or <c>null</c> when this is a protobuf frame.
    /// </summary>
    internal string? Line { get; }

    /// <summary>
    /// The exchange <see cref="Line"/> was framed for, or <c>null</c> when this is a protobuf frame.
    /// </summary>
    internal TextReplyCapture? Capture { get; }

    /// <inheritdoc />
    WireMessage IInboundMessage<WireMessage>.Data => this;
}
