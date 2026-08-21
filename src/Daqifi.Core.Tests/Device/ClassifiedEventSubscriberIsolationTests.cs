using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for issue #560: <see cref="DaqifiDevice.StatusMessageReceived"/> and
/// <see cref="DaqifiDevice.StreamMessageReceived"/> used to wrap the *entire* multicast delegate in a
/// single try/catch. .NET invokes a multicast delegate's invocation list in order and stops at the
/// first exception, so that only isolated the device from a faulting subscriber — not subscribers from
/// each other. A throwing first subscriber silently starved every subscriber added after it, for the
/// life of the connection. The fix walks the invocation list and isolates each subscriber individually.
/// </summary>
public class ClassifiedEventSubscriberIsolationTests
{
    [Fact]
    public void AThrowingStatusSubscriber_DoesNotStarveASubscriberAddedAfterIt()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var secondSubscriberFrames = 0;

        device.StatusMessageReceived += _ => throw new InvalidOperationException("first subscriber misbehaves");
        device.StatusMessageReceived += _ => secondSubscriberFrames++;

        device.InvokeStatusMessage(StatusFrame());
        device.InvokeStatusMessage(StatusFrame());

        Assert.Equal(2, secondSubscriberFrames);
    }

    [Fact]
    public void AThrowingStreamSubscriber_DoesNotStarveASubscriberAddedAfterIt()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var secondSubscriberFrames = 0;

        device.StreamMessageReceived += _ => throw new InvalidOperationException("first subscriber misbehaves");
        device.StreamMessageReceived += _ => secondSubscriberFrames++;

        device.InvokeStreamMessage(StreamFrame());
        device.InvokeStreamMessage(StreamFrame());

        Assert.Equal(2, secondSubscriberFrames);
    }

    [Fact]
    public void AThrowingMiddleSubscriber_StillLetsEarlierAndLaterSubscribersRun()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var order = new List<string>();

        device.StatusMessageReceived += _ => order.Add("first");
        device.StatusMessageReceived += _ => throw new InvalidOperationException("middle subscriber misbehaves");
        device.StatusMessageReceived += _ => order.Add("third");

        device.InvokeStatusMessage(StatusFrame());

        Assert.Equal(new[] { "first", "third" }, order);
    }

    [Fact]
    public void AThrowingSubscriber_StillLetsTheClassifiedEventReachTheUndifferentiatedEvent()
    {
        // A misbehaving StatusMessageReceived subscriber must not prevent MessageReceived (or the
        // stream decode path, for StreamMessageReceived) from firing for the same frame.
        var device = new RaiseProbeDevice("TestDevice");

        device.StatusMessageReceived += _ => throw new InvalidOperationException("misbehaves");
        device.MessageReceived += (_, _) => device.UndifferentiatedRaisesSeen++;

        device.InvokeStatusMessage(StatusFrame());

        Assert.Equal(1, device.UndifferentiatedRaisesSeen);
    }

    [Fact]
    public void MultipleThrowingSubscribers_AllRunAndAllAreContained()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var runs = 0;

        device.StatusMessageReceived += _ => { runs++; throw new InvalidOperationException("a"); };
        device.StatusMessageReceived += _ => { runs++; throw new InvalidOperationException("b"); };
        device.StatusMessageReceived += _ => { runs++; throw new InvalidOperationException("c"); };

        var escaped = Record.Exception(() => device.InvokeStatusMessage(StatusFrame()));

        Assert.Null(escaped);
        Assert.Equal(3, runs);
    }

    [Fact]
    public void ASingleSubscriber_StillWorksUnchanged()
    {
        var device = new RaiseProbeDevice("TestDevice");
        var frames = 0;
        device.StatusMessageReceived += _ => frames++;

        device.InvokeStatusMessage(StatusFrame());

        Assert.Equal(1, frames);
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

    private sealed class RaiseProbeDevice : DaqifiStreamingDevice
    {
        public RaiseProbeDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public int UndifferentiatedRaisesSeen { get; set; }

        public void InvokeStatusMessage(DaqifiOutMessage message) => OnStatusMessageReceived(message);

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }
}
