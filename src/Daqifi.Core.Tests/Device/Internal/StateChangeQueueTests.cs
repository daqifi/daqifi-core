using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Tests.TestSupport;
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
    public void APublishAlone_LeavesWhatIsBehindItForLater()
    {
        var raised = new List<DeviceState>();
        var queue = Queue(e => raised.Add(e.CurrentState));
        var first = queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting);
        var second = queue.Enqueue(DeviceState.Connecting, DeviceState.Disconnected);
        queue.Publish(second);

        queue.PublishAlone(first);
        Assert.Equal([DeviceState.Connecting], raised);

        queue.DeliverRest();
        Assert.Equal([DeviceState.Connecting, DeviceState.Disconnected], raised);
    }

    [Fact]
    public void APublishAlone_NeverDeliversAnEarlierChange()
    {
        // Issue #1035: a disconnect that starts just after a connect has published its outcome holds
        // the lifecycle gate, and a subscriber to that outcome may be waiting for the gate.
        var raised = new List<DeviceState>();
        var queue = Queue(e => raised.Add(e.CurrentState));
        var connecting = queue.Enqueue(DeviceState.Disconnected, DeviceState.Connecting);
        var connected = queue.Enqueue(DeviceState.Connecting, DeviceState.Connected);
        var disconnecting = queue.Enqueue(DeviceState.Connected, DeviceState.Disconnecting);
        queue.Publish(connected);
        queue.PublishAlone(connecting);

        // Connected is at the head now, published, and nobody is delivering.
        queue.PublishAlone(disconnecting);
        Assert.Equal([DeviceState.Connecting], raised);

        queue.DeliverRest();
        Assert.Equal([DeviceState.Connecting, DeviceState.Connected, DeviceState.Disconnecting], raised);
    }

    [Fact]
    public void APublishAloneBehindAnUnpublishedChange_IsDeliveredByThatChangesAuthor()
    {
        var raised = new List<DeviceState>();
        var queue = Queue(e => raised.Add(e.CurrentState));
        var connected = queue.Enqueue(DeviceState.Connecting, DeviceState.Connected);
        var disconnecting = queue.Enqueue(DeviceState.Connected, DeviceState.Disconnecting);

        queue.PublishAlone(disconnecting);
        Assert.Empty(raised);

        queue.Publish(connected);
        Assert.Equal([DeviceState.Connected, DeviceState.Disconnecting], raised);
    }

    [Fact]
    public async Task APublishAloneWhileAnotherThreadDelivers_IsLeftToThatThread()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var deliveredOn = new List<(DeviceState State, int Thread)>();
        var queue = Queue(e =>
        {
            deliveredOn.Add((e.CurrentState, Environment.CurrentManagedThreadId));
            if (e.CurrentState == DeviceState.Connected)
            {
                entered.Set();
                release.Wait(WaitUntil.DefaultTimeout);
            }
        });
        var connected = queue.Enqueue(DeviceState.Connecting, DeviceState.Connected);
        var delivering = Task.Run(() => queue.Publish(connected));
        Assert.True(entered.Wait(WaitUntil.DefaultTimeout));

        queue.PublishAlone(queue.Enqueue(DeviceState.Connected, DeviceState.Disconnecting));
        queue.DeliverRest();
        Assert.Equal([DeviceState.Connected], deliveredOn.Select(d => d.State));

        release.Set();
        await delivering.WaitAsync(WaitUntil.DefaultTimeout);
        Assert.Equal([DeviceState.Connected, DeviceState.Disconnecting], deliveredOn.Select(d => d.State));
        Assert.Single(deliveredOn.Select(d => d.Thread).Distinct());
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
