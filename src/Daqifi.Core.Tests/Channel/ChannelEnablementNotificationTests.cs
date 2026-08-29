using Daqifi.Core.Channel;

namespace Daqifi.Core.Tests.Channel;

/// <summary>
/// Tests for <see cref="IChannelEnablementNotifier"/>, the hook that lets a device notice a caller
/// writing <see cref="IChannel.IsEnabled"/> straight onto a channel (issue #490).
/// </summary>
public class ChannelEnablementNotificationTests
{
    public static TheoryData<IChannel> Channels() => new()
    {
        new AnalogChannel(0),
        new DigitalChannel(0),
    };

    [Theory]
    [MemberData(nameof(Channels))]
    public void ChangingIsEnabled_Notifies(IChannel channel)
    {
        var notifications = 0;
        ((IChannelEnablementNotifier)channel).EnablementChanged += () => notifications++;

        channel.IsEnabled = true;

        Assert.Equal(1, notifications);
    }

    /// <summary>
    /// A write that does not change the value is not a change. Status messages re-assert the same
    /// enabled mask constantly, and treating each one as a change would throw away the caches this
    /// notification exists to keep valid.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void WritingTheSameValue_DoesNotNotify(IChannel channel)
    {
        channel.IsEnabled = true;

        var notifications = 0;
        ((IChannelEnablementNotifier)channel).EnablementChanged += () => notifications++;

        channel.IsEnabled = true;
        channel.IsEnabled = true;

        Assert.Equal(0, notifications);
    }

    /// <summary>
    /// The notification is raised outside the channel's own lock. A handler that reads the channel
    /// back — which is the obvious thing for a handler to do — would otherwise re-enter that lock
    /// from the thread already holding it; that happens to be legal for a <c>Monitor</c>, but it
    /// makes the contract depend on the lock being reentrant. Asserting the value is readable and
    /// already updated pins both halves.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheHandlerCanReadTheChannelBack_AndSeesTheNewValue(IChannel channel)
    {
        bool? observed = null;
        ((IChannelEnablementNotifier)channel).EnablementChanged += () => observed = channel.IsEnabled;

        channel.IsEnabled = true;

        Assert.True(observed);
    }

    /// <summary>
    /// The lock really is released before the handler runs, not merely re-entered by the same
    /// thread. <see cref="TheHandlerCanReadTheChannelBack_AndSeesTheNewValue"/> cannot tell those
    /// apart — its read-back is reentrant, so it passes either way, as its own remarks concede.
    /// Reading the channel from a <em>different</em> thread while the handler is still on the
    /// stack does tell them apart: that thread has to wait for the lock, so it completes at once
    /// if the notification is raised outside and blocks until the timeout if it is not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheHandlerRunsWithTheChannelLockAlreadyReleased(IChannel channel)
    {
        var readFromAnotherThreadCompleted = false;

        ((IChannelEnablementNotifier)channel).EnablementChanged += () =>
        {
            // Blocks the handler until the other thread has taken and released the channel's
            // lock, so the assertion below is about the state of the lock during the callback
            // rather than after the setter has returned.
            var read = Task.Run(() => channel.Name);
            readFromAnotherThreadCompleted = read.Wait(TimeSpan.FromSeconds(10));
        };

        channel.IsEnabled = true;

        Assert.True(
            readFromAnotherThreadCompleted,
            "A second thread could not read the channel while the EnablementChanged handler was " +
            "running, so the notification was raised while the channel's lock was still held.");
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void UnsubscribingStopsNotifications(IChannel channel)
    {
        var notifications = 0;
        void Handler() => notifications++;

        ((IChannelEnablementNotifier)channel).EnablementChanged += Handler;
        channel.IsEnabled = true;
        ((IChannelEnablementNotifier)channel).EnablementChanged -= Handler;
        channel.IsEnabled = false;

        Assert.Equal(1, notifications);
    }
}
