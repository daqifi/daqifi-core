using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Puts a channel into a state that only the device's commands and frames put it into otherwise.
/// </summary>
internal static class ChannelTestExtensions
{
    /// <summary>Sets <see cref="IChannel.IsEnabled"/> without sending anything.</summary>
    public static void SetEnabled(this IChannel channel, bool enabled)
        => ((IChannelEnablementNotifier)channel).SetEnabled(enabled);

    /// <summary>Delivers a sample, as the decoder does.</summary>
    public static void SetActiveSample(this IChannel channel, double value, DateTime timestamp)
    {
        switch (channel)
        {
            case AnalogChannel analog:
                analog.SetActiveSample(new DataSample(timestamp, value) { Scaling = analog.Scaling });
                break;
            case DigitalChannel digital:
                digital.SetActiveSample(new DataSample(timestamp, value));
                break;
            default:
                throw new NotSupportedException($"{channel.GetType().Name} takes no samples.");
        }
    }
}
