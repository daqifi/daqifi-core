using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System.Linq;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for <see cref="DaqifiDevice.ChannelStateVersion"/> and the end-to-end consequence it
/// exists for: the streaming decoder caches which analog channels are active, and that cache has to
/// follow every way the answer can change (issue #490).
/// </summary>
/// <remarks>
/// The decoder's own view of this is covered directly in <c>StreamFrameDecoderTests</c> against a
/// fake host. These run it through a real <see cref="DaqifiStreamingDevice"/>, because the wiring
/// between a channel being written to and the version moving is the part a fake cannot vouch for.
/// </remarks>
public class ChannelStateVersionTests
{
    #region The version itself

    [Fact]
    public void PopulatingChannelsFromAStatus_MovesTheVersion()
    {
        var device = new VersionProbeDevice("TestDevice");
        var before = device.ChannelStateVersion;

        device.PopulateChannelsFromStatus(Status(analogCount: 2));

        Assert.NotEqual(before, device.ChannelStateVersion);
    }

    [Fact]
    public void WritingIsEnabledOnAChannelTheDeviceOwns_MovesTheVersion()
    {
        var device = CreateDevice(analogCount: 2);
        var before = device.ChannelStateVersion;

        device.Channels.First(c => c.ChannelNumber == 0).IsEnabled = true;

        Assert.NotEqual(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// A write that changes nothing is not a change. Without this the version would move on every
    /// status message that re-asserts the same enabled mask, throwing away the cache the version
    /// exists to protect.
    /// </summary>
    [Fact]
    public void WritingTheSameEnabledValue_DoesNotMoveTheVersion()
    {
        var device = CreateDevice(analogCount: 2);
        var channel = device.Channels.First(c => c.ChannelNumber == 0);
        channel.IsEnabled = true;
        var before = device.ChannelStateVersion;

        channel.IsEnabled = true;

        Assert.Equal(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// Digital channels are part of the snapshot the decoder caches, so their enablement has to
    /// move the version as well.
    /// </summary>
    [Fact]
    public void WritingIsEnabledOnADigitalChannel_MovesTheVersion()
    {
        var device = CreateDevice(analogCount: 1, digitalCount: 2);
        var before = device.ChannelStateVersion;

        device.Channels.First(c => c.Type == ChannelType.Digital).IsEnabled = true;

        Assert.NotEqual(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// A status that describes the same channels the device already has is not a change. The
    /// populator reuses those instances, so nothing derived from them can have gone stale — moving
    /// the version anyway would throw away a live stream's cache on every status poll.
    /// </summary>
    [Fact]
    public void RepopulatingWithTheSameChannels_DoesNotMoveTheVersion()
    {
        var device = CreateDevice(analogCount: 2, digitalCount: 2);
        var before = device.ChannelStateVersion;

        device.PopulateChannelsFromStatus(Status(analogCount: 2, digitalCount: 2));
        device.PopulateChannelsFromStatus(Status(analogCount: 2, digitalCount: 2));

        Assert.Equal(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// …but a status that changes the enabled mask on those same instances still moves it, through
    /// the channels' own notifications rather than through the membership check.
    /// </summary>
    [Fact]
    public void RepopulatingWithADifferentEnabledMask_StillMovesTheVersion()
    {
        var device = CreateDevice(analogCount: 2);
        var before = device.ChannelStateVersion;

        var status = Status(analogCount: 2);
        status.AnalogInPortEnabled = Google.Protobuf.ByteString.CopyFrom(0b0000_0011);
        device.PopulateChannelsFromStatus(status);

        Assert.NotEqual(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// A channel count change replaces the membership, which no per-channel notification covers.
    /// </summary>
    [Fact]
    public void RepopulatingWithADifferentChannelCount_MovesTheVersion()
    {
        var device = CreateDevice(analogCount: 2);
        var before = device.ChannelStateVersion;

        device.PopulateChannelsFromStatus(Status(analogCount: 3));

        Assert.NotEqual(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// A channel the device no longer owns must not keep bumping the version — that is a
    /// subscription leak, and it would make every write to a long-discarded channel invalidate a
    /// live stream's cache.
    /// </summary>
    [Fact]
    public void AChannelDroppedByRepopulation_NoLongerMovesTheVersion()
    {
        var device = CreateDevice(analogCount: 2);
        var dropped = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 1);

        // Repopulate with a single analog channel, so channel 1 is no longer the device's.
        device.PopulateChannelsFromStatus(Status(analogCount: 1));
        Assert.DoesNotContain(dropped, device.Channels);

        var before = device.ChannelStateVersion;
        dropped.IsEnabled = !dropped.IsEnabled;

        Assert.Equal(before, device.ChannelStateVersion);
    }

    /// <summary>
    /// The mirror image: an instance the populator <em>reused</em> across a repopulation is still
    /// the device's channel, and detaching-then-reattaching it must leave it subscribed exactly
    /// once — not zero times (silent staleness) and not twice.
    /// </summary>
    [Fact]
    public void AChannelReusedByRepopulation_StillMovesTheVersion_Once()
    {
        var device = CreateDevice(analogCount: 2);
        device.PopulateChannelsFromStatus(Status(analogCount: 2));

        var reused = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0);
        var before = device.ChannelStateVersion;

        reused.IsEnabled = !reused.IsEnabled;

        Assert.Equal(before + 1, device.ChannelStateVersion);
    }

    #endregion

    #region End-to-end through the decode path

    /// <summary>
    /// The behaviour success criterion of #490: a channel enabled mid-stream takes effect on the
    /// next frame. Runs through the public API a consumer actually uses.
    /// </summary>
    [Fact]
    public void EnablingAChannelMidStream_TakesEffectOnTheNextFrame()
    {
        var device = CreateDevice(analogCount: 2);
        var ai0 = Analog(device, 0);
        var ai1 = Analog(device, 1);
        ai1.IsEnabled = true;

        device.StartStreaming();
        device.InvokeStreamMessage(Frame(1000, 5.0f));
        Assert.Equal(5.0, ai1.ActiveSample!.Value, 3);
        Assert.Null(ai0.ActiveSample);

        ai0.IsEnabled = true;
        device.InvokeStreamMessage(Frame(2000, 1.0f, 2.0f));

        Assert.Equal(1.0, ai0.ActiveSample!.Value, 3);
        Assert.Equal(2.0, ai1.ActiveSample!.Value, 3);
    }

    /// <summary>
    /// A status message arriving mid-stream repopulates channels, and the frames after it must be
    /// decoded against the new set.
    /// </summary>
    [Fact]
    public void RepopulatingChannelsMidStream_TakesEffectOnTheNextFrame()
    {
        var device = CreateDevice(analogCount: 1);
        Analog(device, 0).IsEnabled = true;

        device.StartStreaming();
        device.InvokeStreamMessage(Frame(1000, 1.0f));
        Assert.Equal(1.0, Analog(device, 0).ActiveSample!.Value, 3);

        // Two analog channels now, both enabled by the device-reported mask.
        var status = Status(analogCount: 2);
        status.AnalogInPortEnabled = Google.Protobuf.ByteString.CopyFrom(0b0000_0011);
        device.PopulateChannelsFromStatus(status);

        device.InvokeStreamMessage(Frame(2000, 3.0f, 4.0f));

        Assert.Equal(3.0, Analog(device, 0).ActiveSample!.Value, 3);
        Assert.Equal(4.0, Analog(device, 1).ActiveSample!.Value, 3);
    }

    #endregion

    #region Helpers

    private static VersionProbeDevice CreateDevice(int analogCount, int digitalCount = 0)
    {
        var device = new VersionProbeDevice("TestDevice");
        device.Connect();
        device.PopulateChannelsFromStatus(Status(analogCount, digitalCount));
        return device;
    }

    private static DaqifiOutMessage Status(int analogCount, int digitalCount = 0)
    {
        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = (uint)analogCount,
            DigitalPortNum = (uint)digitalCount,
            AnalogInRes = 65535,
        };

        for (var i = 0; i < analogCount; i++)
        {
            status.AnalogInPortRange.Add(1.0f);
        }

        return status;
    }

    private static IAnalogChannel Analog(DaqifiStreamingDevice device, int number) =>
        (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == number);

    private static DaqifiOutMessage Frame(uint timestamp, params float[] values)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        foreach (var value in values)
        {
            frame.AnalogInDataFloat.Add(value);
        }
        return frame;
    }

    /// <summary>
    /// A streaming device that swallows outbound SCPI (so streaming can be started without a
    /// transport) and lets a frame be injected straight into the stream handler.
    /// </summary>
    private sealed class VersionProbeDevice : DaqifiStreamingDevice
    {
        public VersionProbeDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }

    #endregion
}
