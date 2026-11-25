using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Tests.TestSupport;
using System.Collections.Generic;
using Xunit;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceTests
{
    [Fact]
    public void Constructor_InitializesPropertiesCorrectly()
    {
        // Arrange
        const string deviceName = "TestDevice";

        // Act
        var device = new TransportlessDevice(deviceName);

        // Assert
        Assert.Equal(deviceName, device.Name);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void Connect_ChangesStateAndRaisesEvents()
    {
        // Arrange
        var device = new TransportlessDevice("TestDevice");
        var statusChanges = new List<DeviceState>();
        device.StateChanged += (_, args) =>
        {
            statusChanges.Add(args.CurrentState);
        };

        // Act
        device.Connect();

        // Assert
        Assert.Equal(2, statusChanges.Count);
        Assert.Equal(DeviceState.Connecting, statusChanges[0]);
        Assert.Equal(DeviceState.Connected, statusChanges[1]);
        Assert.Equal(DeviceState.Connected, device.State);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void Disconnect_ChangesStateAndRaisesEvents()
    {
        // Arrange
        var device = new TransportlessDevice("TestDevice");
        device.Connect(); // Connect first

        var receivedArgs = new List<DeviceStateChangedEventArgs>();
        device.StateChanged += (_, args) =>
        {
            receivedArgs.Add(args);
        };

        // Act
        device.Disconnect();

        // Assert
        Assert.Equal(
            [DeviceState.Disconnecting, DeviceState.Disconnected],
            receivedArgs.Select(args => args.CurrentState));
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void OnStatusMessageReceived_RaisesClassifiedStatusMessageReceived()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        DaqifiOutMessage? classified = null;
        object? raisedBy = null;
        device.StatusMessageReceived += (sender, e) => (raisedBy, classified) = (sender, e.Message);

        var status = new DaqifiOutMessage { AnalogInPortNum = 1 };

        // Act
        device.InvokeStatusMessage(status);

        // Assert
        Assert.Same(status, classified);
        Assert.Same(device, raisedBy);
    }

    [Fact]
    public void OnStreamMessageReceived_RaisesClassifiedStreamMessageReceived()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        DaqifiOutMessage? classified = null;
        object? raisedBy = null;
        device.StreamMessageReceived += (sender, e) => (raisedBy, classified) = (sender, e.Message);

        var stream = new DaqifiOutMessage { MsgTimeStamp = 1 };

        // Act
        device.InvokeStreamMessage(stream);

        // Assert
        Assert.Same(stream, classified);
        Assert.Same(device, raisedBy);
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_CompletesAndAppliesTheReply_WhenAStatusArrives()
    {
        // The point of the API: health is not live -- the device answers when asked and is
        // otherwise silent -- so there has to be a supported way to ask (issue #535).
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        var refresh = device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(5));

        // The reply, delivered the way the consumer loop delivers one.
        device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 42 });

        await refresh;

        // It must actually ask -- the whole point is that the device says nothing unprompted.
        Assert.Contains(device.SentCommands, c => c.Contains("SYSInfoPB", StringComparison.Ordinal));

        // Metadata is updated before the event is raised, so it is already applied here.
        Assert.Equal(42, device.Metadata.Health.BatteryPercent);
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_WhenTheDeviceStaysSilent_Throws()
    {
        // A silent device must surface as a timeout rather than hanging the caller. This is
        // the realistic failure: the device sends nothing at all unless asked, so a lost
        // request produces silence, not an error frame.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        await Assert.ThrowsAsync<TimeoutException>(
            () => device.RefreshDeviceStatusAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_WhenNotConnected_Throws()
    {
        var device = new TestableDaqifiDevice("TestDevice");

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_IsNotBlockedByAThrowingStatusSubscriber()
    {
        // .NET stops invoking a multicast delegate at the first exception, and
        // RaiseClassifiedEvent wraps the WHOLE list in one try/catch -- so a consumer that
        // subscribed earlier and throws would have prevented the refresh from ever seeing its
        // own reply. It would then time out while the status it asked for had already been
        // applied to Metadata, which is the most confusing failure this API could produce.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        device.StatusMessageReceived += (_, _) => throw new InvalidOperationException("boom");

        var refresh = device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(2));
        device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 55 });

        await refresh;

        Assert.Equal(55, device.Metadata.Health.BatteryPercent);
    }

    /// <summary>
    /// The deadline the two-caller refresh tests hand to <c>RefreshDeviceStatusAsync</c>, and the
    /// longest they wait for anything themselves. A hang-breaker, not an expectation. A refresh's
    /// deadline also covers the time it spends queued behind another caller, so a short one can
    /// run out on a loaded runner before the test gets round to answering the first caller; the
    /// queued caller then completes with a timeout that no reply caused, and every assertion
    /// about what a reply does to it is reading the wrong thing.
    /// </summary>
    private static readonly TimeSpan RefreshHangBreaker = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RefreshDeviceStatusAsync_ConcurrentCalls_AreNotCompletedByEachOthersReply()
    {
        // Both callers subscribe to the same multicast StatusMessageReceived event, so without
        // serialization ONE incoming frame -- which answers only one of the two requests --
        // completes both, and the loser returns a success not tied to its own request.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        var first = device.RefreshDeviceStatusAsync(RefreshHangBreaker);

        // The second call must be queued behind the first, not racing it: at this point only
        // one request can have gone out.
        var second = device.RefreshDeviceStatusAsync(RefreshHangBreaker);

        await Task.Delay(50);
        Assert.Single(device.SentCommands);
        Assert.False(second.IsCompleted);

        // One reply satisfies the first caller only.
        device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 11 });
        await first;

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        // The second caller's own request goes out once it holds the gate, and its own reply
        // completes it. Waited for rather than assumed: the second caller is resumed on the
        // thread pool, and how soon that happens after the first lets go is the runner's
        // business, not this test's.
        await WaitUntil.ThatAsync(
            () => device.SentCommands.Count == 2,
            "the queued refresh never sent its own request once the first caller had finished",
            RefreshHangBreaker);
        Assert.False(second.IsCompleted);

        device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 22 });
        await second;

        Assert.Equal(22, device.Metadata.Health.BatteryPercent);
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_UnsubscribesAfterCompleting()
    {
        // The handler is added per call, so a caller polling in a loop would otherwise
        // accumulate one subscription per refresh for the life of the connection.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        for (var i = 0; i < 3; i++)
        {
            var refresh = device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(5));
            device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 7 });
            await refresh;
        }

        // A status arriving with no refresh outstanding must not fault anything, and the
        // device must still be usable afterwards.
        var ex = Record.Exception(() => device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 9 }));

        Assert.Null(ex);
        Assert.Equal(9, device.Metadata.Health.BatteryPercent);
    }

    [Fact]
    public async Task RefreshDeviceStatusAsync_WhenTheDeviceIsDisposedMidRefresh_NeitherWaiterIsStranded()
    {
        // Closing the app -- or an `await using` scope unwinding -- while a refresh is in
        // flight is an ordinary thing to do, and two callers can legitimately be on this API at
        // once. Both must survive the teardown:
        //
        //  * the one holding the gate keeps the reply it already asked for, and
        //  * the one queued behind it is still handed the gate when the first lets go.
        //
        // Both of those depend on ReleaseResources leaving _statusRefreshGate alone.
        // SemaphoreSlim.Dispose() would drop the queued waiter without faulting it -- the
        // second caller would never be woken and would sit out its whole deadline -- and would
        // make the first caller's unconditional Release() throw ObjectDisposedException over
        // the top of a status that had already arrived and been applied to Metadata. This test
        // is what fails if that Dispose() is ever added back.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        var first = device.RefreshDeviceStatusAsync(RefreshHangBreaker);

        await Task.Delay(50);
        Assert.Single(device.SentCommands);

        // A second caller, now queued on the gate rather than racing the first.
        var second = device.RefreshDeviceStatusAsync(RefreshHangBreaker);

        await Task.Delay(50);
        Assert.Single(device.SentCommands);
        Assert.False(second.IsCompleted);

        device.Dispose();

        // The reply the device had already put on the wire lands after the teardown.
        device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 33 });

        await first;
        Assert.Equal(33, device.Metadata.Health.BatteryPercent);

        // The queued caller is woken the moment the first lets go, gets as far as the send,
        // and is told plainly that the device went away. The exception TYPE is the whole
        // assertion: a stranded waiter would instead sit on the gate until its own deadline and
        // report a TimeoutException, which says nothing true about what happened. No stopwatch
        // beside it -- how long a woken caller takes to be scheduled is not what is under test.
        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => second);

        // It never reached the wire, so the first caller's request is still the only one sent.
        Assert.Single(device.SentCommands);
    }

    private sealed class TestableDaqifiDevice : DaqifiDevice
    {
        public TestableDaqifiDevice(string name) : base(name)
        {
        }

        /// <summary>Commands the device was asked to send, in order.</summary>
        public List<string> SentCommands { get; } = new();

        /// <summary>
        /// Captures instead of transmitting: this double has no transport, and the base
        /// implementation throws without one. Recording the text also lets a test assert
        /// WHICH command was sent, not merely that the call did not throw.
        /// </summary>
        /// <remarks>
        /// The connectivity guard is kept. It is the one piece of the real <c>Send</c> that a
        /// test can observe the absence of: without it the double happily "sends" on a
        /// disconnected or disposed device, and a test asserting what a caller sees after
        /// teardown would be asserting something the production path cannot do.
        /// </remarks>
        public override void Send<T>(IOutboundMessage<T> message)
        {
            EnsureConnected();

            if (message is IOutboundMessage<string> text)
            {
                SentCommands.Add(text.Data);
            }
        }

        public void InvokeStatusMessage(DaqifiOutMessage message) => OnStatusMessageReceived(message);

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);
    }
}