using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// Builds the on-the-wire byte buffers the framing and consumer benchmarks read from: varint32
/// length-prefixed <see cref="DaqifiOutMessage"/> frames, the way the device sends them.
/// </summary>
internal static class SyntheticFrames
{
    /// <summary>
    /// Analog channels per frame. Sixteen is a fully populated Nyquist, and it sets the frame size
    /// that decides how many frames land in one read.
    /// </summary>
    public const int AnalogChannelCount = 16;

    /// <summary>
    /// Device-clock ticks between frames: 50,000 at the hardware's 50 MHz timestamp frequency, so
    /// the synthetic stream is a 1 kHz one.
    /// </summary>
    private const uint TicksPerFrame = 50_000;

    /// <summary>
    /// A buffer of <paramref name="frameCount"/> length-prefixed frames.
    /// </summary>
    /// <param name="frameCount">How many frames to write.</param>
    /// <param name="truncateLastFrame">
    /// When true the final frame's payload is cut in half after its length prefix has been written,
    /// producing the partial frame a read that lands mid-frame leaves behind. The prefix is
    /// deliberately intact: a parser must decline the frame because the bytes have not arrived yet,
    /// not because the prefix is malformed.
    /// </param>
    public static byte[] BuildFrameBuffer(int frameCount, bool truncateLastFrame = false)
    {
        using var buffer = new MemoryStream();

        for (var frame = 0; frame < frameCount; frame++)
        {
            var message = new DaqifiOutMessage { MsgTimeStamp = (uint)(frame + 1) * TicksPerFrame };
            for (var channel = 0; channel < AnalogChannelCount; channel++)
            {
                message.AnalogInDataFloat.Add(1.0f + channel * 0.01f);
            }

            var payload = message.ToByteArray();
            var coded = new CodedOutputStream(buffer, leaveOpen: true);
            coded.WriteLength(payload.Length);
            coded.Flush();

            var isLast = frame == frameCount - 1;
            var length = truncateLastFrame && isLast ? payload.Length / 2 : payload.Length;
            buffer.Write(payload, 0, length);
        }

        return buffer.ToArray();
    }
}
