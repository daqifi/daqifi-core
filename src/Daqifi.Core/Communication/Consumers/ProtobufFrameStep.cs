namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// The outcome of a single <see cref="ProtobufMessageParser.TryReadFrame"/> step over a buffer.
/// </summary>
/// <remarks>
/// The stepping form exists for callers that want each frame as it is decoded rather than a whole
/// buffer's worth at once — see issue #697, where an SD card log's first sample cost a full parse
/// of the leading 64 KB read buffer.
/// </remarks>
internal enum ProtobufFrameStep
{
    /// <summary>
    /// A complete frame was decoded. The caller should take it and step again from the
    /// reported offset.
    /// </summary>
    Frame,

    /// <summary>
    /// No frame begins at the offset that was stepped from. The parser advanced past bytes it
    /// has ruled out — a malformed or implausible length prefix, or a body that would not
    /// deserialize — and the caller should step again from the reported offset. No frame is
    /// produced for this step.
    /// </summary>
    Resync,

    /// <summary>
    /// The buffer holds no further complete frame; anything left is a partial frame still
    /// arriving. The offset does not advance, so the caller should read more data and keep the
    /// bytes from the last advanced offset onwards.
    /// </summary>
    EndOfBuffer
}
