using Daqifi.Core.Channel;
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
    /// <summary>
    /// Unit tests for the async-with-<see cref="CancellationToken"/> surface added to
    /// <see cref="IStreamingDevice"/> and <see cref="IDevice"/> by issue #460: each
    /// <c>...Async</c> twin honors an already-cancelled token before doing anything, and
    /// otherwise performs exactly what its synchronous counterpart does.
    /// </summary>
    public class DaqifiStreamingDeviceAsyncSurfaceTests
    {
        private static TestableDaqifiStreamingDevice CreateConnectedDevice(int analogChannels = 4, int digitalChannels = 4)
        {
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.PopulateChannelsFromStatus(new DaqifiOutMessage
            {
                AnalogInPortNum = (uint)analogChannels,
                AnalogInRes = 65535,
                DigitalPortNum = (uint)digitalChannels
            });
            device.Connect();
            device.SentMessages.Clear();
            return device;
        }

        private static IChannel AnalogChannelAt(DaqifiStreamingDevice device, int channelNumber) =>
            device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == channelNumber);

        private static IChannel DigitalChannelAt(DaqifiStreamingDevice device, int channelNumber) =>
            device.Channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == channelNumber);

        private static CancellationToken CanceledToken()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        #region Streaming

        [Fact]
        public async Task StartStreamingAsync_SendsStartStreamingCommand()
        {
            var device = CreateConnectedDevice();

            await device.StartStreamingAsync();

            Assert.True(device.IsStreaming);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.StartStreaming(device.StreamingFrequency).Data, sent.Data);
        }

        [Fact]
        public async Task StopStreamingAsync_SendsStopStreamingCommand()
        {
            var device = CreateConnectedDevice();
            device.StartStreaming();
            device.SentMessages.Clear();

            await device.StopStreamingAsync();

            Assert.False(device.IsStreaming);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.StopStreaming.Data, sent.Data);
        }

        [Fact]
        public async Task StartStreamingAsync_CanceledToken_ThrowsWithoutSending()
        {
            var device = CreateConnectedDevice();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.StartStreamingAsync(CanceledToken()));

            Assert.False(device.IsStreaming);
            Assert.Empty(device.SentMessages);
        }

        #endregion

        #region Channel management

        [Fact]
        public async Task EnableChannelAsync_Analog_SetsIsEnabledAndSendsBitmask()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);

            await device.EnableChannelAsync(channel);

            Assert.True(channel.IsEnabled);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("1").Data, sent.Data);
        }

        [Fact]
        public async Task EnableChannelsAsync_SendsAtMostOneCommandPerChannelType()
        {
            var device = CreateConnectedDevice();
            var channels = new[] { AnalogChannelAt(device, 0), DigitalChannelAt(device, 0) };

            await device.EnableChannelsAsync(channels);

            Assert.True(channels[0].IsEnabled);
            Assert.True(channels[1].IsEnabled);
            Assert.Equal(2, device.SentMessages.Count);
        }

        [Fact]
        public async Task DisableChannelAsync_ClearsIsEnabled()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);
            device.EnableChannel(channel);
            device.SentMessages.Clear();

            await device.DisableChannelAsync(channel);

            Assert.False(channel.IsEnabled);
        }

        [Fact]
        public async Task DisableAllChannelsAsync_ClearsEveryChannel()
        {
            var device = CreateConnectedDevice();
            device.EnableChannel(AnalogChannelAt(device, 0));
            device.EnableChannel(DigitalChannelAt(device, 0));
            device.SentMessages.Clear();

            await device.DisableAllChannelsAsync();

            Assert.All(device.Channels, c => Assert.False(c.IsEnabled));
        }

        [Fact]
        public async Task EnableChannelAsync_CanceledToken_ThrowsWithoutEnabling()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.EnableChannelAsync(channel, CanceledToken()));

            Assert.False(channel.IsEnabled);
            Assert.Empty(device.SentMessages);
        }

        #endregion

        #region DIO

        [Fact]
        public async Task SetDioDirectionAsync_SetsDirection()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 0);

            await device.SetDioDirectionAsync(channel, ChannelDirection.Output);

            Assert.Equal(ChannelDirection.Output, channel.Direction);
        }

        [Fact]
        public async Task SetDioValueAsync_SendsExpectedCommand()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 0);
            device.SetDioDirection(channel, ChannelDirection.Output);
            device.SentMessages.Clear();

            await device.SetDioValueAsync(channel, true);

            Assert.Single(device.SentMessages);
        }

        #endregion

        #region PWM

        [Fact]
        public async Task SetPwmFrequencyAsync_UpdatesPwmFrequencyHz()
        {
            var device = CreateConnectedDevice();

            await device.SetPwmFrequencyAsync(1000);

            Assert.Equal(1000, device.PwmFrequencyHz);
        }

        [Fact]
        public async Task SetPwmDutyCycleAsync_AndSetPwmEnabledAsync_SendCommands()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 0);
            device.SetDioDirection(channel, ChannelDirection.Output);
            device.SentMessages.Clear();

            await device.SetPwmDutyCycleAsync(channel, 50);
            await device.SetPwmEnabledAsync(channel, true);

            Assert.Equal(2, device.SentMessages.Count);
        }

        #endregion

        #region Analog output and reboot

        [Fact]
        public async Task SetAnalogOutputAsync_SendsCommand()
        {
            var device = CreateConnectedDevice();

            await device.SetAnalogOutputAsync(0, 1.5);

            Assert.NotEmpty(device.SentMessages);
        }

        [Fact]
        public async Task RebootAsync_SendsRebootCommandAndDisconnects()
        {
            var device = CreateConnectedDevice();

            await device.RebootAsync();

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.RebootDevice.Data, sent.Data);
            Assert.False(device.IsConnected);
        }

        [Fact]
        public async Task RebootAsync_CanceledToken_ThrowsWithoutDisconnecting()
        {
            var device = CreateConnectedDevice();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.RebootAsync(CanceledToken()));

            Assert.True(device.IsConnected);
            Assert.Empty(device.SentMessages);
        }

        #endregion

        #region IDevice.ConnectAsync / DisconnectAsync are genuine members, not DIM shims

        [Fact]
        public async Task IDevice_ConnectAsync_DisconnectAsync_AreCallableThroughTheInterface()
        {
            IDevice device = new TestableDaqifiStreamingDevice("TestDevice");

            await device.ConnectAsync();
            Assert.True(device.IsConnected);

            await device.DisconnectAsync();
            Assert.False(device.IsConnected);
        }

        #endregion

        #region IDevice : IAsyncDisposable

        [Fact]
        public async Task IDevice_IsAsyncDisposable_AndDisposeAsyncDisconnects()
        {
            IDevice device = new TestableDaqifiStreamingDevice("TestDevice");
            await device.ConnectAsync();

            await device.DisposeAsync();

            Assert.False(device.IsConnected);
        }

        #endregion

        #region IStreamingDevice default Async implementations (no override) still work

        [Fact]
        public async Task IStreamingDevice_DefaultAsyncStartStreaming_DelegatesToSyncMethod()
        {
            // MinimalStreamingDevice implements only the synchronous members, relying entirely on
            // IStreamingDevice's default-interface-method bodies for the Async twins. Reached only
            // through the interface type, matching how a consumer coding against IStreamingDevice
            // would call it.
            IStreamingDevice device = new MinimalStreamingDevice();

            await device.StartStreamingAsync();

            Assert.True(device.IsStreaming);
        }

        [Fact]
        public async Task IStreamingDevice_DefaultAsyncStartStreaming_CanceledToken_DoesNotStart()
        {
            IStreamingDevice device = new MinimalStreamingDevice();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.StartStreamingAsync(CanceledToken()));

            Assert.False(device.IsStreaming);
        }

        [Fact]
        public async Task IStreamingDevice_ExtendsIConfirmingDeviceAdministration()
        {
            IStreamingDevice device = new MinimalStreamingDevice();
            Assert.IsAssignableFrom<IConfirmingDeviceAdministration>(device);

            // Reachable directly, no cast needed.
            await device.SaveAdcCalibrationAsync();
        }

        #endregion

        /// <summary>
        /// The smallest possible <see cref="IStreamingDevice"/> implementer: every member not
        /// exercised by these tests is a no-op, and none of the new <c>...Async</c> members are
        /// overridden — every one of them must resolve to the interface's default implementation.
        /// </summary>
        private sealed class MinimalStreamingDevice : IStreamingDevice
        {
            public string Name => "Minimal";
            public IPAddress? IpAddress => null;
            public bool IsConnected { get; private set; } = true;
            public ConnectionStatus Status => IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
            public int StreamingFrequency { get; set; }
            public bool IsStreaming { get; private set; }
            public int PwmFrequencyHz => 0;

            public event EventHandler<DeviceStatusEventArgs>? StatusChanged { add { } remove { } }
            public event EventHandler<MessageReceivedEventArgs>? MessageReceived { add { } remove { } }
            public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred { add { } remove { } }

            public void Connect() => IsConnected = true;
            public void Disconnect() => IsConnected = false;

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Connect();
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                Disconnect();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                Disconnect();
                return ValueTask.CompletedTask;
            }

            public void Send<T>(IOutboundMessage<T> message) { }

            public void StartStreaming() => IsStreaming = true;
            public void StopStreaming() => IsStreaming = false;
            public void EnableChannel(IChannel channel) { }
            public void EnableChannels(IEnumerable<IChannel> channels) { }
            public void DisableChannel(IChannel channel) { }
            public void DisableAllChannels() { }
            public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
            public void SetDioValue(IChannel channel, bool value) { }
            public void SetPwmEnabled(IChannel channel, bool enabled) { }
            public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent) { }
            public void SetPwmFrequency(int frequencyHz) { }
            public void SetAnalogOutput(int channelNumber, double voltage) { }
            public void Reboot() => Disconnect();

            public void SaveAdcCalibration() { }
            public void LoadAdcCalibration() { }
            public void SetAdcCalibrationSlope(int channelNumber, double calM) { }
            public void SetAdcCalibrationOffset(int channelNumber, double calB) { }
            public void SaveFactoryAdcCalibration() { }
            public void LoadFactoryAdcCalibration() { }
            public void UseAdcCalibration(int bank) { }
            public void SaveVoltagePrecision() { }
            public void LoadVoltagePrecision() { }

            public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) { SaveAdcCalibration(); return Task.CompletedTask; }
            public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) { LoadAdcCalibration(); return Task.CompletedTask; }
            public Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default) { SetAdcCalibrationSlope(channelNumber, calM); return Task.CompletedTask; }
            public Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default) { SetAdcCalibrationOffset(channelNumber, calB); return Task.CompletedTask; }
            public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) { SaveFactoryAdcCalibration(); return Task.CompletedTask; }
            public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) { LoadFactoryAdcCalibration(); return Task.CompletedTask; }
            public Task UseAdcCalibrationAsync(int bank, CancellationToken cancellationToken = default) { UseAdcCalibration(bank); return Task.CompletedTask; }
            public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) { SaveVoltagePrecision(); return Task.CompletedTask; }
            public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) { LoadVoltagePrecision(); return Task.CompletedTask; }
        }

        /// <summary>
        /// A testable <see cref="DaqifiStreamingDevice"/> that captures sent messages instead of
        /// writing to a transport, mirroring the pattern in <see cref="DaqifiStreamingDeviceTests"/>.
        /// </summary>
        private sealed class TestableDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            public TestableDaqifiStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                // Capture instead of sending so tests run without a real connection.
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }
        }
    }
}
