using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System.Collections.Generic;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for when <see cref="DaqifiDevice.MessageReceived"/> is raised (issue #490).
/// </summary>
/// <remarks>
/// The event arguments are an allocation per frame, which on a streaming device is one per sample
/// period, so they are built only when there is a subscriber. What these pin is that nothing else
/// about the frame's journey depends on one: <see cref="DaqifiDevice.OnMessageReceived"/> and the
/// classified events see every frame either way.
/// </remarks>
public class UndifferentiatedMessageRaiseTests
{
    [Fact]
    public void WithNoSubscriber_AnOverrideStillSeesEveryFrame()
    {
        using var device = new RaiseProbeDevice("TestDevice");

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(2, device.FramesSeen);
    }

    [Fact]
    public void WithASubscriber_BothStatusAndStreamFramesAreRaised()
    {
        using var device = new RaiseProbeDevice("TestDevice");
        var received = new List<(object? Sender, DaqifiOutMessage Message)>();
        device.MessageReceived += (sender, e) => received.Add((sender, e.Message));
        var status = StatusFrame();
        var stream = StreamFrame();

        device.InvokeStatusMessage(status);
        device.InvokeStreamMessage(stream);

        Assert.Equal([(device, status), (device, stream)], received);
        Assert.Equal(2, device.FramesSeen);
    }

    /// <summary>
    /// The classified events are the ones Core's own consumers and the decode path ride on, so they
    /// have to stay independent of whether anyone listens to the undifferentiated event.
    /// </summary>
    [Fact]
    public void TheClassifiedEventsStillFireWithNoUndifferentiatedSubscriber()
    {
        using var device = new RaiseProbeDevice("TestDevice");

        var statusFrames = 0;
        var streamFrames = 0;
        device.StatusMessageReceived += (_, _) => statusFrames++;
        device.StreamMessageReceived += (_, _) => streamFrames++;

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(1, statusFrames);
        Assert.Equal(1, streamFrames);
    }

    private static DaqifiOutMessage StatusFrame()
    {
        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = 1,
            AnalogInRes = 65535,
        };
        status.AnalogInPortRange.Add(1.0f);
        return status;
    }

    private static DaqifiOutMessage StreamFrame()
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        frame.AnalogInDataFloat.Add(1.0f);
        return frame;
    }

    /// <summary>
    /// Counts how many frames reach <see cref="DaqifiDevice.OnMessageReceived"/>.
    /// </summary>
    private sealed class RaiseProbeDevice : DaqifiStreamingDevice
    {
        public RaiseProbeDevice(string name) : base(name)
        {
        }

        public int FramesSeen { get; private set; }

        public void InvokeStatusMessage(DaqifiOutMessage message) => OnStatusMessageReceived(message);

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        protected override void OnMessageReceived(DaqifiOutMessage message)
        {
            FramesSeen++;
            base.OnMessageReceived(message);
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }
}
