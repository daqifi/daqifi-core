using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>Issue #1035: the ordering rules behind <see cref="IDevice.StateChanged"/>.</summary>
public class StateChangeQueueTests
{
    [Fact]
    public void AChangeThatIsNotPublishedYet_HoldsBackTheOnesBehindIt()
    {
        var raised = new List<DeviceState>();
        var queue = Queue(e => raised.Add(e.CurrentState));
        var first = queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting);
        var second = queue.Enqueue(DeviceState.Connecting, DeviceState.Connected);

        queue.Publish(second);
        Assert.Empty(raised);

        queue.Publish(first);
        Assert.Equal([DeviceState.Connecting, DeviceState.Connected], raised);
    }

    [Fact]
    public void AChangePublishedFromInsideADelivery_IsDeliveredAfterIt()
    {
        var raised = new List<string>();
        StateChangeQueue queue = null!;
        queue = Queue(e =>
        {
            raised.Add($"enter {e.CurrentState}");
            if (e.CurrentState == DeviceState.Connecting)
            {
                queue.Publish(queue.Enqueue(DeviceState.Connecting, DeviceState.Connected));
            }

            raised.Add($"leave {e.CurrentState}");
        });

        queue.Publish(queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting));

        Assert.Equal(["enter Connecting", "leave Connecting", "enter Connected", "leave Connected"], raised);
    }

    [Fact]
    public void APublishAlone_LeavesWhatIsBehindItForDeliver()
    {
        var raised = new List<DeviceState>();
        var queue = Queue(e => raised.Add(e.CurrentState));
        var first = queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting);
        var second = queue.Enqueue(DeviceState.Connecting, DeviceState.Disconnected);
        queue.Publish(second);

        queue.PublishAlone(first);
        Assert.Equal([DeviceState.Connecting], raised);

        queue.Deliver();
        Assert.Equal([DeviceState.Connecting, DeviceState.Disconnected], raised);
    }

    [Fact]
    public void ASubscriberThatThrows_IsReported_AndCostsTheOthersNothing()
    {
        var thrown = new InvalidOperationException("a badly behaved subscriber");
        var raised = new List<DeviceState>();
        var errors = new List<Exception>();
        EventHandler<DeviceStateChangedEventArgs> subscribers = (_, _) => throw thrown;
        subscribers += (_, e) => raised.Add(e.CurrentState);
        var queue = new StateChangeQueue(this, () => subscribers, errors.Add);

        queue.Publish(queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting));

        Assert.Equal([DeviceState.Connecting], raised);
        Assert.Same(thrown, Assert.Single(errors));
    }

    private StateChangeQueue Queue(Action<DeviceStateChangedEventArgs> raise) =>
        new(this, () => (_, e) => raise(e), error => Assert.Fail(error.ToString()));
}
