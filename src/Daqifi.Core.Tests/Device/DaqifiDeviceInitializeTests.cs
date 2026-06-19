using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceInitializeTests
    {
        [Fact]
        public async Task InitializeAsync_SendsAllConfigCommands()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — the 4 text commands are sent via ExecuteTextCommandAsync
            // and GetDeviceInfo is sent via direct Send()
            var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();

            Assert.Contains(sentData, d => d.Contains("SYSTem:ECHO -1"));
            Assert.Contains(sentData, d => d.Contains("SYSTem:StopStreamData"));
            Assert.Contains(sentData, d => d.Contains("SYSTem:POWer:STATe 1"));
            Assert.Contains(sentData, d => d.Contains("SYSTem:STReam:FORmat 0"));
            Assert.Contains(sentData, d => d.Contains("SYSTem:SYSInfoPB?"));
        }

        [Fact]
        public async Task InitializeAsync_SendsGetDeviceInfo()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — GetDeviceInfo is sent as a direct Send after ExecuteTextCommandAsync
            var directSends = device.DirectSentMessages.Select(m => m.Data).ToList();
            Assert.Contains(directSends, d => d.Contains("SYSTem:SYSInfoPB?"));
        }

        [Fact]
        public async Task InitializeAsync_SetsStateToReady()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.Equal(DeviceState.Ready, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenAlreadyInitialized_DoesNotSendCommandsAgain()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();
            await device.InitializeAsync();
            var firstCallCount = device.DirectSentMessages.Count;

            // Act — second call
            await device.InitializeAsync();

            // Assert — no additional commands sent on second call
            Assert.Equal(firstCallCount, device.DirectSentMessages.Count);
        }

        [Fact]
        public async Task InitializeAsync_WhenDisconnected_Throws()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            // Not connected

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.InitializeAsync());
            Assert.Equal("Device must be connected before initialization.", ex.Message);
        }

        [Fact]
        public async Task InitializeAsync_WhenDeviceReturnsScpiError_Throws()
        {
            // Arrange — device returns a -200 error line during init
            var device = new TestableDaqifiDevice("TestDevice",
                textCommandResponse: new[] { "**ERROR: -200, \"Execution error\"\r\n" });
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.InitializeAsync());

            Assert.Contains("-200", ex.Message);
            Assert.Equal(DeviceState.Error, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenDeviceReturnsScpiError_SetsStateToError()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice",
                textCommandResponse: new[] { "**ERROR: -200, \"Execution error\"\r\n" });
            device.Connect();

            // Act
            try { await device.InitializeAsync(); } catch (InvalidOperationException) { }

            // Assert
            Assert.Equal(DeviceState.Error, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenChannelsPopulate_ExposesPopulatedChannels()
        {
            // Arrange — device responds to GetDeviceInfo by populating channels
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — initialization blocked until ChannelsPopulated fired, so the
            // returned device is fully populated rather than empty
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(4, device.Channels.Count); // 2 analog + 2 digital
        }

        [Fact]
        public async Task InitializeAsync_WhenChannelsNeverPopulate_ThrowsTimeoutExceptionAndResends()
        {
            // Arrange — device never reports channel configuration
            var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
            device.Connect();

            // Act & Assert — a clear timeout, not a silently-unpopulated device
            await Assert.ThrowsAsync<TimeoutException>(
                () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

            Assert.Equal(DeviceState.Error, device.State);
            Assert.Empty(device.Channels);
            // GetDeviceInfo is re-sent while waiting (initial request + at least one retry).
            Assert.True(device.DeviceInfoRequestCount >= 2);
        }

        [Fact]
        public async Task InitializeAsync_OnReconnect_WaitsForFreshStatusInsteadOfStaleChannels()
        {
            // Arrange — first init populates channels; the instance is then reused across a
            // reconnect (e.g. FirmwareUpdateService post-reset wake). Disconnect leaves the
            // prior session's channels in place, so initialization must wait for a fresh
            // status rather than short-circuiting on the stale channels.
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();
            await device.InitializeAsync();
            Assert.NotEmpty(device.Channels); // stale channels now linger

            device.Disconnect();
            device.Connect();

            // The reconnected device does not report a fresh status.
            device.PopulateChannelsOnDeviceInfo = false;

            // Act & Assert — times out instead of returning stale channels.
            await Assert.ThrowsAsync<TimeoutException>(
                () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));
            Assert.Equal(DeviceState.Error, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenCancelledDuringWait_ThrowsOperationCanceledException()
        {
            // Arrange — device never populates, so the wait loop is observing cancellation
            var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
            device.Connect();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.InitializeAsync(TimeSpan.FromSeconds(5), cts.Token));
        }

        /// <summary>
        /// A testable DaqifiDevice that captures sent messages without needing a real transport.
        /// Overrides ExecuteTextCommandAsync to bypass transport requirements in unit tests.
        /// </summary>
        private class TestableDaqifiDevice : DaqifiDevice
        {
            private readonly IReadOnlyList<string> _textCommandResponse;

            /// <summary>
            /// All messages sent via direct Send() calls.
            /// </summary>
            public List<IOutboundMessage<string>> DirectSentMessages { get; } = new();

            /// <summary>
            /// Number of GetDeviceInfo (SYSInfoPB?) requests observed.
            /// </summary>
            public int DeviceInfoRequestCount { get; private set; }

            /// <summary>
            /// When true, a GetDeviceInfo request synchronously populates channels (simulating
            /// the device's status response). Settable so tests can toggle the behavior between
            /// initialization attempts.
            /// </summary>
            public bool PopulateChannelsOnDeviceInfo { get; set; }

            public TestableDaqifiDevice(
                string name,
                IPAddress? ipAddress = null,
                IReadOnlyList<string>? textCommandResponse = null,
                bool populateChannelsOnDeviceInfo = true)
                : base(name, ipAddress)
            {
                _textCommandResponse = textCommandResponse ?? Array.Empty<string>();
                PopulateChannelsOnDeviceInfo = populateChannelsOnDeviceInfo;
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    DirectSentMessages.Add(stringMessage);

                    // Simulate the device responding to GetDeviceInfo (SYSInfoPB?) with a
                    // protobuf status message that populates channels. The real flow does this
                    // via the protobuf consumer, which has no backing transport in unit tests.
                    if (stringMessage.Data.Contains("SYSInfoPB"))
                    {
                        DeviceInfoRequestCount++;
                        if (PopulateChannelsOnDeviceInfo)
                        {
                            PopulateChannelsFromStatus(new DaqifiOutMessage
                            {
                                AnalogInPortNum = 2,
                                DigitalPortNum = 2
                            });
                        }
                    }
                }
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                // Run the setup action so that Send() calls inside it are captured
                setupAction();
                return Task.FromResult(_textCommandResponse);
            }
        }
    }
}
