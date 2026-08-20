using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceTests
    {
        [Fact]
        public void Constructor_InitializesPropertiesCorrectly()
        {
            // Arrange
            const string deviceName = "TestDevice";
            var ipAddress = IPAddress.Parse("192.168.1.1");

            // Act
            var device = new DaqifiDevice(deviceName, ipAddress);

            // Assert
            Assert.Equal(deviceName, device.Name);
            Assert.Equal(ipAddress, device.IpAddress);
            Assert.Equal(ConnectionStatus.Disconnected, device.Status);
            Assert.False(device.IsConnected);
        }

        [Fact]
        public void Connect_ChangesStatusAndRaisesEvents()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");
            var statusChanges = new List<ConnectionStatus>();
            device.StatusChanged += (_, args) =>
            {
                statusChanges.Add(args.Status);
            };

            // Act
            device.Connect();

            // Assert
            Assert.Equal(2, statusChanges.Count);
            Assert.Equal(ConnectionStatus.Connecting, statusChanges[0]);
            Assert.Equal(ConnectionStatus.Connected, statusChanges[1]);
            Assert.Equal(ConnectionStatus.Connected, device.Status);
            Assert.True(device.IsConnected);
        }

        [Fact]
        public void Disconnect_ChangesStatusAndRaisesEvent()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");
            device.Connect(); // Connect first

            var receivedArgs = new List<DeviceStatusEventArgs>();
            device.StatusChanged += (_, args) =>
            {
                receivedArgs.Add(args);
            };

            // Act
            device.Disconnect();

            // Assert
            var arg = Assert.Single(receivedArgs);
            Assert.Equal(ConnectionStatus.Disconnected, arg.Status);
            Assert.Equal(ConnectionStatus.Disconnected, device.Status);
            Assert.False(device.IsConnected);
        }

        [Fact]
        public void Send_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");

            // Act & Assert
            Assert.Throws<DeviceNotConnectedException>(() => device.Send(new Daqifi.Core.Communication.Messages.ScpiMessage("")));
        }

        [Fact]
        public void OnStatusMessageReceived_RaisesClassifiedStatusMessageReceived()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            DaqifiOutMessage? classified = null;
            device.StatusMessageReceived += m => classified = m;

            var status = new DaqifiOutMessage { AnalogInPortNum = 1 };

            // Act
            device.InvokeStatusMessage(status);

            // Assert
            Assert.Same(status, classified);
        }

        [Fact]
        public void OnStreamMessageReceived_RaisesClassifiedStreamMessageReceived()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            DaqifiOutMessage? classified = null;
            device.StreamMessageReceived += m => classified = m;

            var stream = new DaqifiOutMessage { MsgTimeStamp = 1 };

            // Act
            device.InvokeStreamMessage(stream);

            // Assert
            Assert.Same(stream, classified);
        }

        [Fact]
        public void OnStatusMessageReceived_SubscriberException_StillRaisesMessageReceived()
        {
            // A misbehaving StatusMessageReceived subscriber must not prevent the
            // undifferentiated MessageReceived event from firing for the same frame.
            var device = new TestableDaqifiDevice("TestDevice");
            device.StatusMessageReceived += _ => throw new InvalidOperationException("boom");

            Daqifi.Core.Device.MessageReceivedEventArgs? raised = null;
            device.MessageReceived += (_, e) => raised = e;

            var status = new DaqifiOutMessage { AnalogInPortNum = 1 };

            var ex = Record.Exception(() => device.InvokeStatusMessage(status));

            Assert.Null(ex);
            Assert.NotNull(raised);
        }

        [Fact]
        public void OnStreamMessageReceived_SubscriberException_StillRaisesMessageReceived()
        {
            // A misbehaving StreamMessageReceived subscriber must not prevent the
            // undifferentiated MessageReceived event from firing for the same frame.
            var device = new TestableDaqifiDevice("TestDevice");
            device.StreamMessageReceived += _ => throw new InvalidOperationException("boom");

            Daqifi.Core.Device.MessageReceivedEventArgs? raised = null;
            device.MessageReceived += (_, e) => raised = e;

            var stream = new DaqifiOutMessage { MsgTimeStamp = 1 };

            var ex = Record.Exception(() => device.InvokeStreamMessage(stream));

            Assert.Null(ex);
            Assert.NotNull(raised);
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
        public async Task RefreshDeviceStatusAsync_ConcurrentCalls_AreNotCompletedByEachOthersReply()
        {
            // Both callers subscribe to the same multicast StatusMessageReceived event, so without
            // serialization ONE incoming frame -- which answers only one of the two requests --
            // completes both, and the loser returns a success not tied to its own request.
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            var first = device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(5));

            // The second call must be queued behind the first, not racing it: at this point only
            // one request can have gone out.
            var second = device.RefreshDeviceStatusAsync(TimeSpan.FromSeconds(5));

            await Task.Delay(50);
            Assert.Single(device.SentCommands);
            Assert.False(second.IsCompleted);

            // One reply satisfies the first caller only.
            device.InvokeStatusMessage(new DaqifiOutMessage { BattStatus = 11 });
            await first;

            await Task.Delay(50);
            Assert.False(second.IsCompleted);

            // The second caller's own request goes out once it holds the gate, and its own reply
            // completes it.
            Assert.Equal(2, device.SentCommands.Count);
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

        private sealed class TestableDaqifiDevice : DaqifiDevice
        {
            public TestableDaqifiDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
            {
            }

            /// <summary>Commands the device was asked to send, in order.</summary>
            public List<string> SentCommands { get; } = new();

            /// <summary>
            /// Captures instead of transmitting: this double has no transport, and the base
            /// implementation throws without one. Recording the text also lets a test assert
            /// WHICH command was sent, not merely that the call did not throw.
            /// </summary>
            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> text)
                {
                    SentCommands.Add(text.Data);
                }
            }

            public void InvokeStatusMessage(DaqifiOutMessage message) => OnStatusMessageReceived(message);

            public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);
        }
    }
}