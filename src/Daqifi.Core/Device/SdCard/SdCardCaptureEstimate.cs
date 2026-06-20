using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Describes a planned SD-card logging capture and estimates how much space it will consume.
/// Used by <see cref="SdCardSpaceCheck"/> to decide whether to warn the user before logging starts.
/// </summary>
/// <remarks>
/// The estimate is intentionally best-effort. <see cref="DefaultBytesPerSamplePerChannel"/> reflects the
/// raw ADC sample width; the on-card footprint of a Protobuf- or JSON-encoded stream is larger because of
/// per-message framing and timestamps. Callers that know their encoding overhead should pass a larger
/// <c>bytesPerSamplePerChannel</c> so the warning errs on the side of caution.
/// </remarks>
public sealed class SdCardCaptureEstimate
{
    /// <summary>
    /// The default per-channel sample width in bytes: the raw 16-bit ADC resolution. Real encoded logs
    /// are larger; this is a floor, not an exact figure.
    /// </summary>
    public const int DefaultBytesPerSamplePerChannel = 2;

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardCaptureEstimate"/> class.
    /// </summary>
    /// <param name="frequencyHz">The streaming/sampling frequency in samples-per-second per channel. Must be positive.</param>
    /// <param name="channelCount">The number of enabled channels being recorded. Must be at least 1.</param>
    /// <param name="duration">The planned capture duration. Must be positive.</param>
    /// <param name="bytesPerSamplePerChannel">
    /// The number of bytes each channel contributes per sample. Defaults to
    /// <see cref="DefaultBytesPerSamplePerChannel"/> (the raw 16-bit ADC width). Must be positive.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any parameter is non-positive.</exception>
    public SdCardCaptureEstimate(
        int frequencyHz,
        int channelCount,
        TimeSpan duration,
        int bytesPerSamplePerChannel = DefaultBytesPerSamplePerChannel)
    {
        if (frequencyHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Frequency must be positive.");
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), channelCount, "Channel count must be at least 1.");
        }

        if (bytesPerSamplePerChannel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSamplePerChannel), bytesPerSamplePerChannel, "Bytes per sample must be positive.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be positive.");
        }

        FrequencyHz = frequencyHz;
        ChannelCount = channelCount;
        Duration = duration;
        BytesPerSamplePerChannel = bytesPerSamplePerChannel;
    }

    /// <summary>Gets the streaming/sampling frequency in samples-per-second per channel.</summary>
    public int FrequencyHz { get; }

    /// <summary>Gets the number of enabled channels being recorded.</summary>
    public int ChannelCount { get; }

    /// <summary>Gets the planned capture duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the number of bytes each channel contributes per sample.</summary>
    public int BytesPerSamplePerChannel { get; }

    /// <summary>
    /// Gets the estimated write rate in bytes per second:
    /// <c>FrequencyHz × ChannelCount × BytesPerSamplePerChannel</c>, clamped to <see cref="long.MaxValue"/>
    /// if the product would overflow. Clamping keeps the warning layer correct: an overflow that wrapped
    /// negative would make a capture wrongly appear to fit and suppress the truncation ETA.
    /// </summary>
    public long BytesPerSecond
    {
        get
        {
            try
            {
                return checked((long)FrequencyHz * ChannelCount * BytesPerSamplePerChannel);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }
    }

    /// <summary>
    /// Gets the estimated total bytes the capture will write, clamped to <see cref="long.MaxValue"/> if it
    /// would otherwise overflow.
    /// </summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = BytesPerSecond * Duration.TotalSeconds;
            return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
        }
    }
}
