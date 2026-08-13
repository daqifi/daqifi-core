using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for when <see cref="DaqifiDevice.MessageReceived"/> is raised (issue #490).
/// </summary>
/// <remarks>
/// Every inbound frame used to be wrapped in an <see cref="IInboundMessage{T}"/> so it could be
/// offered to this event, whether or not anything was listening — an allocation per frame, which on
/// a streaming device is one per sample period. The wrapper is now built only when there is a
/// subscriber; what these pin is that nothing else about the frame's journey changed.
/// </remarks>
public class UndifferentiatedMessageRaiseTests
{
    [Fact]
    public void WithNoSubscriber_TheUndifferentiatedEventIsNotRaised()
    {
        var device = new RaiseProbeDevice("TestDevice");

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(0, device.UndifferentiatedRaises);
    }

    [Fact]
    public void WithASubscriber_BothStatusAndStreamFramesAreRaised()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var received = new List<object?>();
        device.MessageReceived += (_, e) => received.Add(e.Message.Data);

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(2, received.Count);
        Assert.All(received, data => Assert.IsType<DaqifiOutMessage>(data));
        Assert.Equal(2, device.UndifferentiatedRaises);
    }

    /// <summary>
    /// The classified events are the ones Core's own consumers and the decode path ride on, so they
    /// have to stay unconditional — the subscriber check applies only to the undifferentiated event
    /// whose payload had to be allocated.
    /// </summary>
    [Fact]
    public void TheClassifiedEventsStillFireWithNoUndifferentiatedSubscriber()
    {
        var device = new RaiseProbeDevice("TestDevice");

        var statusFrames = 0;
        var streamFrames = 0;
        device.StatusMessageReceived += _ => statusFrames++;
        device.StreamMessageReceived += _ => streamFrames++;

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(1, statusFrames);
        Assert.Equal(1, streamFrames);
        Assert.Equal(0, device.UndifferentiatedRaises);
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
    /// Counts how many times a frame actually made it as far as
    /// <see cref="DaqifiDevice.OnMessageReceived"/> — the point past which the wrapper has already
    /// been allocated.
    /// </summary>
    private sealed class RaiseProbeDevice : DaqifiStreamingDevice
    {
        public RaiseProbeDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public int UndifferentiatedRaises { get; private set; }

        public void InvokeStatusMessage(DaqifiOutMessage message) => OnStatusMessageReceived(message);

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        protected override void OnMessageReceived(IInboundMessage<object> message)
        {
            UndifferentiatedRaises++;
            base.OnMessageReceived(message);
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }
}
