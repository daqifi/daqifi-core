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

            // The stream-disruptive commands are the default because this session is assumed to
            // own the device; the opt-out below must not change that (#385).
            Assert.False(device.PreserveActiveStream);
        }

        [Fact]
        public async Task InitializeAsync_WithPreserveActiveStream_OmitsStreamDisruptiveCommands()
        {
            // Arrange — a secondary session attaching to a device another session may already be
            // streaming from.
            var device = new TestableDaqifiDevice("TestDevice") { PreserveActiveStream = true };
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — nothing that halts, powers, or reconfigures the device's single global
            // stream is sent.
            var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();

            Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:StopStreamData"));
            Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:POWer:STATe"));
            Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:STReam:FORmat"));

            // ...but the session is still fully set up: echo off so its own replies parse, and the
            // identity query that populates channels.
            Assert.Contains(sentData, d => d.Contains("SYSTem:ECHO -1"));
            Assert.Contains(sentData, d => d.Contains("SYSTem:SYSInfoPB?"));
        }

        [Fact]
        public async Task InitializeAsync_WithPreserveActiveStream_StillProducesReadyPopulatedDevice()
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice") { PreserveActiveStream = true };
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — skipping the disruptive commands must not cost the caller a usable session
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(4, device.Channels.Count); // 2 analog + 2 digital
        }

        [Fact]
        public async Task InitializeAsync_WithPreserveActiveStream_StreamingUsb_DoesNotRouteStreamToUsb()
        {
            // Arrange — over USB the streaming device normally claims the stream with
            // SYSTem:STReam:INTerface 0, which would take data away from a session already
            // receiving it over WiFi.
            var device = new TestableStreamingDevice("TestDevice") { PreserveActiveStream = true };
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.DoesNotContain(device.SentData, d => d.Contains("SYSTem:STReam:INTerface"));
            Assert.Equal(0, device.UsbStepAttemptCount);
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(4, device.Channels.Count);
        }

        [Fact]
        public async Task InitializeAsync_WhenPreserveActiveStreamChangesMidInitialization_KeepsTheDecisionItStartedWith()
        {
            // Arrange — an observing initialization whose flag is flipped to false after it has
            // begun (a concurrent initialization on the same instance, or a caller mutating the
            // property). The decision belongs to the operation, so the USB routing step must still
            // be skipped: honoring the late change would steal a stream this session promised not
            // to touch.
            var device = new TestableStreamingDevice("TestDevice") { PreserveActiveStream = true };
            device.MutateDuringInitialization = () => device.PreserveActiveStream = false;
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.False(device.PreserveActiveStream); // the mutation really did land
            Assert.DoesNotContain(device.SentData, d => d.Contains("SYSTem:STReam:INTerface"));
            Assert.Equal(0, device.UsbStepAttemptCount);
            Assert.Equal(DeviceState.Ready, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenPreserveActiveStreamIsSetMidInitialization_StillTakesControl()
        {
            // Arrange — the mirror case: a normal take-control initialization must not be silently
            // downgraded to observing by a flag set after it started, which would leave the stream
            // routed somewhere this session cannot read.
            var device = new TestableStreamingDevice("TestDevice");
            device.MutateDuringInitialization = () => device.PreserveActiveStream = true;
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.True(device.PreserveActiveStream); // the mutation really did land
            Assert.Contains(device.SentData, d => d.Contains("SYSTem:STReam:INTerface 0"));
            Assert.Contains(device.SentData, d => d.Contains("SYSTem:StopStreamData"));
            Assert.Equal(DeviceState.Ready, device.State);
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
            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.InitializeAsync());
            Assert.Equal("Device must be connected before initialization.", ex.Message);
        }

        [Fact]
        public async Task InitializeAsync_WhenDeviceReturnsScpiError_ThrowsTypedExceptionAfterRetry()
        {
            // Arrange — device returns a -200 error line on every attempt (persistent, not transient)
            var device = new TestableDaqifiDevice("TestDevice",
                textCommandResponse: new[] { "**ERROR: -200, \"Execution error\"\r\n" });
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
                () => device.InitializeAsync());

            Assert.Contains("-200", ex.Message);
            Assert.Equal("**ERROR: -200, \"Execution error\"", ex.LastScpiError);
            Assert.Equal(DeviceState.Error, device.State);
            // One initial attempt plus one retry.
            Assert.Equal(2, device.TextCommandAttemptCount);
        }

        [Theory]
        [InlineData("ERROR -200,\"Execution error\"\r\n")]
        [InlineData("ERROR\t-200,\"Execution error\"\r\n")]
        [InlineData("ERROR: -200,\"Execution error\"\r\n")]
        public async Task InitializeAsync_WhenDeviceReturnsBareErrorWithNonColonDelimiter_ThrowsTypedException(string errorLine)
        {
            // Arrange — a bare "ERROR" token (no "**" prefix) using a space/tab/colon delimiter
            // rather than "**ERROR" must still be recognized as a real SCPI error.
            var device = new TestableDaqifiDevice("TestDevice",
                textCommandResponse: new[] { errorLine });
            device.Connect();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
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
            try { await device.InitializeAsync(); } catch (ScpiInitializationErrorException) { }

            // Assert
            Assert.Equal(DeviceState.Error, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenDeviceReturnsTransientScpiError_RetriesAndSucceeds()
        {
            // Arrange — the first attempt returns a SCPI error, the retry succeeds, simulating
            // the narrow timing race described in issue #310.
            var device = new TestableDaqifiDevice("TestDevice",
                textCommandResponse: Array.Empty<string>(),
                failFirstAttempt: true);
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(2, device.TextCommandAttemptCount);
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

            // Caller-initiated cancellation is not a device fault — state must not flip to Error.
            Assert.NotEqual(DeviceState.Error, device.State);
            Assert.Equal(DeviceState.Connected, device.State);
        }

        [Fact]
        public async Task InitializeAsync_WhenChannelsPopulateAsynchronously_CompletesViaWaitLoop()
        {
            // Arrange — channels arrive on a background thread after a delay (as on real hardware
            // via the consumer thread), forcing the Task.WhenAny wait loop rather than the
            // synchronous short-circuit.
            var device = new TestableDaqifiDevice("TestDevice")
            {
                AsyncPopulationDelay = TimeSpan.FromMilliseconds(150)
            };
            device.Connect();

            // Act
            await device.InitializeAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(4, device.Channels.Count);
        }

        [Fact]
        public async Task InitializeAsync_StreamingUsb_Succeeds_SetsReadyAndRoutesToUsb()
        {
            // Arrange
            var device = new TestableStreamingDevice("TestDevice");
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert — device is ready and the USB stream-interface command was sent
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(4, device.Channels.Count);
            Assert.Contains(device.SentData, d => d.Contains("SYSTem:STReam:INTerface 0"));
        }

        [Fact]
        public async Task InitializeAsync_StreamingUsb_WhenUsbStepReturnsScpiError_SetsErrorNotReady()
        {
            // Arrange — the USB SetStreamInterface step fails on every attempt (persistent, not
            // transient) after base init populated channels
            var device = new TestableStreamingDevice("TestDevice", UsbStepBehavior.ScpiError);
            device.Connect();

            // Act & Assert — failure in the override must not leave the device falsely Ready,
            // and surfaces as the typed exception rather than a bare InvalidOperationException
            var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(() => device.InitializeAsync());
            Assert.Equal(DeviceState.Error, device.State);
            Assert.Equal(2, device.UsbStepAttemptCount);
            Assert.NotNull(ex.LastScpiError);
        }

        [Fact]
        public async Task InitializeAsync_StreamingUsb_WhenUsbStepReturnsTransientScpiError_RetriesAndSucceeds()
        {
            // Arrange — the USB SetStreamInterface step fails once, then succeeds on retry,
            // simulating the narrow timing race described in issue #310.
            var device = new TestableStreamingDevice("TestDevice", UsbStepBehavior.ScpiErrorThenSucceed);
            device.Connect();

            // Act
            await device.InitializeAsync();

            // Assert
            Assert.Equal(DeviceState.Ready, device.State);
            Assert.Equal(2, device.UsbStepAttemptCount);
        }

        [Fact]
        public async Task InitializeAsync_StreamingUsb_WhenCancelledDuringUsbStep_RevertsToConnected()
        {
            // Arrange — cancellation hits the USB step after base init reached the channel wait
            var device = new TestableStreamingDevice("TestDevice", UsbStepBehavior.Cancel);
            device.Connect();

            // Act & Assert — cancellation in the override is not a fault; state reverts, not Error,
            // and not the falsely-Ready state the old override could leave behind.
            await Assert.ThrowsAsync<OperationCanceledException>(() => device.InitializeAsync());
            Assert.Equal(DeviceState.Connected, device.State);
            Assert.NotEqual(DeviceState.Ready, device.State);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1000)]
        public async Task InitializeAsync_WithNonPositiveTimeout_ThrowsArgumentOutOfRangeException(int timeoutMs)
        {
            // Arrange
            var device = new TestableDaqifiDevice("TestDevice");
            device.Connect();

            // Act & Assert — a misconfigured timeout is an argument error, not a device timeout
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.InitializeAsync(TimeSpan.FromMilliseconds(timeoutMs)));

            // Misconfiguration must not flip the device into an error state.
            Assert.Equal(DeviceState.Connected, device.State);
        }

        /// <summary>
        /// A testable DaqifiDevice that captures sent messages without needing a real transport.
        /// Overrides ExecuteTextCommandAsync to bypass transport requirements in unit tests.
        /// </summary>
        private class TestableDaqifiDevice : DaqifiDevice
        {
            private readonly IReadOnlyList<string> _textCommandResponse;
            private readonly bool _failFirstAttempt;

            /// <summary>
            /// All messages sent via direct Send() calls.
            /// </summary>
            public List<IOutboundMessage<string>> DirectSentMessages { get; } = new();

            /// <summary>
            /// Number of GetDeviceInfo (SYSInfoPB?) requests observed.
            /// </summary>
            public int DeviceInfoRequestCount { get; private set; }

            /// <summary>
            /// Number of times the init SCPI setup sequence's ExecuteTextCommandAsync was invoked.
            /// </summary>
            public int TextCommandAttemptCount { get; private set; }

            /// <summary>
            /// When true, a GetDeviceInfo request synchronously populates channels (simulating
            /// the device's status response). Settable so tests can toggle the behavior between
            /// initialization attempts.
            /// </summary>
            public bool PopulateChannelsOnDeviceInfo { get; set; }

            /// <summary>
            /// When set, channels populate asynchronously after this delay on a background thread
            /// (simulating a status that arrives via the consumer thread) instead of synchronously
            /// inside Send. This exercises the production Task.WhenAny wait loop rather than the
            /// synchronous short-circuit.
            /// </summary>
            public TimeSpan? AsyncPopulationDelay { get; set; }

            public TestableDaqifiDevice(
                string name,
                IPAddress? ipAddress = null,
                IReadOnlyList<string>? textCommandResponse = null,
                bool populateChannelsOnDeviceInfo = true,
                bool failFirstAttempt = false)
                : base(name, ipAddress)
            {
                _textCommandResponse = textCommandResponse ?? Array.Empty<string>();
                PopulateChannelsOnDeviceInfo = populateChannelsOnDeviceInfo;
                _failFirstAttempt = failFirstAttempt;
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
                            if (AsyncPopulationDelay is { } delay)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(delay);
                                    PopulateChannelsFromStatus(new DaqifiOutMessage
                                    {
                                        AnalogInPortNum = 2,
                                        DigitalPortNum = 2
                                    });
                                });
                            }
                            else
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
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null)
            {
                // Honor the exchange's prepare phase the way the real device does: it runs first,
                // before anything this exchange sends (#396).
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                // Run the setup action so that Send() calls inside it are captured
                setupAction();
                TextCommandAttemptCount++;

                if (_failFirstAttempt && TextCommandAttemptCount == 1)
                {
                    return new[] { "**ERROR: -200, \"Execution error\"\r\n" };
                }

                return _textCommandResponse;
            }
        }

        /// <summary>
        /// Outcome of the streaming device's USB SetStreamInterface step, used to exercise the
        /// failure paths of the OnDeviceInitializingAsync hook.
        /// </summary>
        private enum UsbStepBehavior
        {
            Succeed,
            ScpiError,
            ScpiErrorThenSucceed,
            Cancel
        }

        [Theory]
        [InlineData(true)]  // observing: the hook returns immediately, no awaitable work
        [InlineData(false)] // take-control: the hook does send SCPI
        public async Task InitializeAsync_WhenCancelledAfterChannelsPopulate_DoesNotReportReady(bool preserveActiveStream)
        {
            // Arrange — cancellation lands at the one seam nothing was guaranteed to observe:
            // after channels populate, during the capability read. Firmware that does not
            // advertise the capability document returns from that read without touching the
            // token, and on the observing path the derived hook then returns immediately too, so
            // a device whose caller had already cancelled still reached Ready.
            using var cts = new CancellationTokenSource();
            var device = new CancelDuringCapabilityReadDevice("TestDevice", cts)
            {
                PreserveActiveStream = preserveActiveStream
            };
            device.Connect();

            // Act & Assert — a cancelled initialization reports cancellation, not success.
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.InitializeAsync(TimeSpan.FromSeconds(5), cts.Token));

            Assert.NotEqual(DeviceState.Ready, device.State);
            Assert.Equal(DeviceState.Connected, device.State);
        }

        /// <summary>
        /// A testable USB streaming device that cancels the supplied token from inside the
        /// capability read — i.e. after channels have populated and before the derived
        /// initialization hook — and returns without observing the token itself, exactly as the
        /// real read does on firmware that does not advertise a capability document.
        /// </summary>
        private class CancelDuringCapabilityReadDevice : DaqifiStreamingDevice
        {
            private readonly CancellationTokenSource _cts;

            public override bool IsUsbConnection => true;

            public CancelDuringCapabilityReadDevice(string name, CancellationTokenSource cts)
                : base(name, (IPAddress?)null)
            {
                _cts = cts;
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage &&
                    stringMessage.Data.Contains("SYSInfoPB"))
                {
                    PopulateChannelsFromStatus(new DaqifiOutMessage
                    {
                        AnalogInPortNum = 2,
                        DigitalPortNum = 2
                    });
                }
            }

            public override Task<Daqifi.Core.Device.Capabilities.CapabilityDocument?> ReadCapabilityDocumentAsync(
                CancellationToken cancellationToken = default)
            {
                _cts.Cancel();
                return Task.FromResult<Daqifi.Core.Device.Capabilities.CapabilityDocument?>(null);
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null)
            {
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }
        }

        [Fact]
        public async Task InitializeAsync_WhenTwoInitializationsOverlapOnOneDevice_EachKeepsItsOwnDecision()
        {
            // Arrange — the race reported against the first cut of this change: the observing
            // decision was held in an instance field, so a second InitializeAsync starting while
            // the first was still in flight overwrote it, and the first initialization's USB
            // routing step then acted on the second one's decision. Reproduced by holding an
            // observing initialization inside its first SCPI exchange — past the point the
            // decision is made, before the routing step — while a take-control initialization
            // starts on the same instance.
            var device = new OverlappingInitDevice("TestDevice");
            device.Connect();

            device.PreserveActiveStream = true;
            var observing = device.InitializeAsync();

            // Wait until the observing call is parked inside its first exchange.
            await device.FirstExchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            device.PreserveActiveStream = false;
            var takingControl = device.InitializeAsync();
            await takingControl.WaitAsync(TimeSpan.FromSeconds(5));

            // Act — let the observing initialization finish, now that the flag has moved under it.
            device.ReleaseFirstExchange();
            await observing.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert — one initialization decided to observe and one to take control, and each
            // reached its own hook with its own decision. A shared field yields {false, false}.
            Assert.Equal(
                new[] { false, true },
                device.HookDecisions.OrderBy(flag => flag).ToArray());
        }

        /// <summary>
        /// A testable USB streaming device that records the decision each initialization passes to
        /// <c>OnDeviceInitializingAsync</c>, and can park its first text exchange so a second
        /// initialization can be started while the first is still in flight.
        /// </summary>
        private class OverlappingInitDevice : DaqifiStreamingDevice
        {
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _exchangeCount;

            /// <summary>Completes once an initialization has entered its first text exchange.</summary>
            public TaskCompletionSource FirstExchangeEntered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>The decision each initialization handed to the derived hook.</summary>
            public System.Collections.Concurrent.ConcurrentBag<bool> HookDecisions { get; } = new();

            public override bool IsUsbConnection => true;

            public OverlappingInitDevice(string name) : base(name, (IPAddress?)null) { }

            public void ReleaseFirstExchange() => _release.TrySetResult();

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage &&
                    stringMessage.Data.Contains("SYSInfoPB"))
                {
                    PopulateChannelsFromStatus(new DaqifiOutMessage
                    {
                        AnalogInPortNum = 2,
                        DigitalPortNum = 2
                    });
                }
            }

            protected override Task OnDeviceInitializingAsync(
                bool preserveActiveStream,
                CancellationToken cancellationToken)
            {
                HookDecisions.Add(preserveActiveStream);
                return Task.CompletedTask;
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null)
            {
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                setupAction();

                // Park only the very first exchange — the one belonging to the initialization the
                // test starts first.
                if (Interlocked.Increment(ref _exchangeCount) == 1)
                {
                    FirstExchangeEntered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                }

                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// A testable DaqifiStreamingDevice (always USB) whose base init populates channels on
        /// GetDeviceInfo and whose USB stream-interface step can be made to succeed, return a SCPI
        /// error, or be canceled.
        /// </summary>
        private class TestableStreamingDevice : DaqifiStreamingDevice
        {
            private readonly UsbStepBehavior _usbStepBehavior;
            private readonly List<string> _sent = new();

            public IReadOnlyList<string> SentData => _sent;

            /// <summary>
            /// Number of times the USB SetStreamInterface step's ExecuteTextCommandAsync was invoked.
            /// </summary>
            public int UsbStepAttemptCount { get; private set; }

            /// <summary>
            /// Invoked once, from inside the first text exchange of initialization — i.e. after
            /// InitializeAsync has captured its PreserveActiveStream decision but before the
            /// derived USB step runs. Lets a test mutate device state mid-initialization.
            /// </summary>
            public Action? MutateDuringInitialization { get; set; }

            private bool _mutationApplied;

            public override bool IsUsbConnection => true;

            public TestableStreamingDevice(string name, UsbStepBehavior usbStepBehavior = UsbStepBehavior.Succeed)
                : base(name, (IPAddress?)null)
            {
                _usbStepBehavior = usbStepBehavior;
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    _sent.Add(stringMessage.Data);
                    if (stringMessage.Data.Contains("SYSInfoPB"))
                    {
                        PopulateChannelsFromStatus(new DaqifiOutMessage
                        {
                            AnalogInPortNum = 2,
                            DigitalPortNum = 2
                        });
                    }
                }
            }

            protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default,
                Func<CancellationToken, Task>? prepareAsync = null)
            {
                // Honor the exchange's prepare phase the way the real device does: it runs first,
                // before anything this exchange sends (#396).
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!_mutationApplied && MutateDuringInitialization != null)
                {
                    _mutationApplied = true;
                    MutateDuringInitialization();
                }

                var before = _sent.Count;
                setupAction();
                var sentThisCall = _sent.Skip(before).ToList();
                var isUsbStep = sentThisCall.Any(d => d.Contains("STReam:INTerface"));

                if (isUsbStep)
                {
                    UsbStepAttemptCount++;

                    switch (_usbStepBehavior)
                    {
                        case UsbStepBehavior.ScpiError:
                            return new[] { "**ERROR: -200, \"Execution error\"\r\n" };
                        case UsbStepBehavior.ScpiErrorThenSucceed:
                            if (UsbStepAttemptCount == 1)
                            {
                                return new[] { "**ERROR: -200, \"Execution error\"\r\n" };
                            }
                            break;
                        case UsbStepBehavior.Cancel:
                            throw new OperationCanceledException();
                    }
                }

                return Array.Empty<string>();
            }
        }
    }
}
