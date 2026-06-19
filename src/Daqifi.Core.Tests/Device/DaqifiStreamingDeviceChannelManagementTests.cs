using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    /// <summary>
    /// Unit tests for the device-level channel-management API on <see cref="DaqifiStreamingDevice"/>
    /// (ADC enable bitmask, DIO direction/value, analog output, reboot).
    /// </summary>
    public class DaqifiStreamingDeviceChannelManagementTests
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

        #region EnableChannel / DisableChannel - Analog bitmask

        [Fact]
        public void EnableChannel_Analog_SetsIsEnabledAndSendsBitmask()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);

            device.EnableChannel(channel);

            Assert.True(channel.IsEnabled);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("1").Data, sent.Data);
        }

        [Fact]
        public void EnableChannel_Analog_AccumulatesFullBitmaskAcrossCalls()
        {
            var device = CreateConnectedDevice();

            device.EnableChannel(AnalogChannelAt(device, 1)); // bit 1 = 2
            device.EnableChannel(AnalogChannelAt(device, 3)); // bit 3 = 8

            // The firmware mask is a set-replace, so the last command must carry the full set (2 + 8 = 10).
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("10").Data, device.SentMessages.Last().Data);
        }

        [Fact]
        public void DisableChannel_Analog_SendsBitmaskWithoutThatChannel()
        {
            var device = CreateConnectedDevice();
            device.EnableChannel(AnalogChannelAt(device, 0));
            device.EnableChannel(AnalogChannelAt(device, 1));
            device.EnableChannel(AnalogChannelAt(device, 2));
            device.SentMessages.Clear();

            device.DisableChannel(AnalogChannelAt(device, 1)); // remaining: 0 and 2 => 1 + 4 = 5

            Assert.False(AnalogChannelAt(device, 1).IsEnabled);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("5").Data, sent.Data);
        }

        [Fact]
        public void EnableChannel_Analog_DoesNotSendDioCommand()
        {
            var device = CreateConnectedDevice();

            device.EnableChannel(AnalogChannelAt(device, 0));

            Assert.DoesNotContain(device.SentMessages, m =>
                m.Data == ScpiMessageProducer.EnableDioPorts().Data ||
                m.Data == ScpiMessageProducer.DisableDioPorts().Data);
        }

        [Fact]
        public void EnableChannel_Analog_AtBitmaskBoundary_ProducesHighBitMask()
        {
            // Channel 31 is the widest bit representable in the 32-bit (1u << n) mask.
            var device = CreateConnectedDevice(analogChannels: 32, digitalChannels: 0);
            var channel = AnalogChannelAt(device, 31);

            device.EnableChannel(channel);

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("2147483648").Data, sent.Data);
        }

        [Fact]
        public void EnableChannel_Analog_BeyondBitmaskRange_ThrowsInvalidOperationException()
        {
            // Channel 32 cannot be encoded in the 32-bit mask, so the deliberate overflow guard fires.
            var device = CreateConnectedDevice(analogChannels: 33, digitalChannels: 0);
            var channel = AnalogChannelAt(device, 32);

            Assert.Throws<InvalidOperationException>(() => device.EnableChannel(channel));
        }

        #endregion

        #region EnableChannels - mixed and batching

        [Fact]
        public void EnableChannels_MultipleAnalog_SendsSingleBitmaskCommand()
        {
            var device = CreateConnectedDevice();
            var channels = new[] { AnalogChannelAt(device, 0), AnalogChannelAt(device, 2) }; // 1 + 4 = 5

            device.EnableChannels(channels);

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("5").Data, sent.Data);
        }

        [Fact]
        public void EnableChannels_Mixed_SendsAdcMaskAndDioEnable()
        {
            var device = CreateConnectedDevice();
            var channels = new[] { AnalogChannelAt(device, 0), DigitalChannelAt(device, 1) };

            device.EnableChannels(channels);

            Assert.Equal(2, device.SentMessages.Count);
            Assert.Contains(device.SentMessages, m => m.Data == ScpiMessageProducer.EnableAdcChannels("1").Data);
            Assert.Contains(device.SentMessages, m => m.Data == ScpiMessageProducer.EnableDioPorts().Data);
        }

        [Fact]
        public void EnableChannels_WithNullCollection_ThrowsArgumentNullException()
        {
            var device = CreateConnectedDevice();

            Assert.Throws<ArgumentNullException>(() => device.EnableChannels(null!));
        }

        [Fact]
        public void EnableChannels_WithNullEntry_ThrowsAndSendsNothing()
        {
            var device = CreateConnectedDevice();
            var channels = new List<IChannel> { AnalogChannelAt(device, 0), null! };

            Assert.Throws<ArgumentException>(() => device.EnableChannels(channels));
            // Validation runs before any mutation, so no command is sent and no state changes.
            Assert.Empty(device.SentMessages);
            Assert.False(AnalogChannelAt(device, 0).IsEnabled);
        }

        [Fact]
        public void EnableChannels_DeferredSequence_IsEnumeratedExactlyOnce()
        {
            var device = CreateConnectedDevice();
            // A single-use sequence throws on a second enumeration; this pins the contract that
            // EnableChannels materializes the input once (validation pass + mutation pass must not
            // re-enumerate the caller's sequence).
            var sequence = new SingleEnumerationSequence(
                AnalogChannelAt(device, 0), AnalogChannelAt(device, 2)); // 1 + 4 = 5

            device.EnableChannels(sequence);

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("5").Data, sent.Data);
        }

        [Fact]
        public void EnableChannels_WithDuplicateChannels_SendsSingleCorrectBitmask()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 2); // bit 2 = 4

            device.EnableChannels(new[] { channel, channel });

            // The mask is recomputed over the device's channel set, so duplicates are idempotent.
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("4").Data, sent.Data);
        }

        #endregion

        #region Digital enable (global)

        [Fact]
        public void EnableChannel_Digital_SendsGlobalDioEnable()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 0);

            device.EnableChannel(channel);

            Assert.True(channel.IsEnabled);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableDioPorts().Data, sent.Data);
        }

        [Fact]
        public void DisableChannel_Digital_KeepsDioEnabledWhileOthersRemain()
        {
            var device = CreateConnectedDevice();
            device.EnableChannel(DigitalChannelAt(device, 0));
            device.EnableChannel(DigitalChannelAt(device, 1));
            device.SentMessages.Clear();

            device.DisableChannel(DigitalChannelAt(device, 0));

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableDioPorts().Data, sent.Data);
        }

        [Fact]
        public void DisableChannel_LastDigital_SendsGlobalDioDisable()
        {
            var device = CreateConnectedDevice();
            device.EnableChannel(DigitalChannelAt(device, 0));
            device.SentMessages.Clear();

            device.DisableChannel(DigitalChannelAt(device, 0));

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.DisableDioPorts().Data, sent.Data);
        }

        #endregion

        #region DisableAllChannels

        [Fact]
        public void DisableAllChannels_ClearsStateAndSendsZeroMaskAndDioDisable()
        {
            var device = CreateConnectedDevice();
            device.EnableChannel(AnalogChannelAt(device, 0));
            device.EnableChannel(DigitalChannelAt(device, 0));
            device.SentMessages.Clear();

            device.DisableAllChannels();

            Assert.All(device.Channels, c => Assert.False(c.IsEnabled));
            Assert.Contains(device.SentMessages, m => m.Data == ScpiMessageProducer.EnableAdcChannels("0").Data);
            Assert.Contains(device.SentMessages, m => m.Data == ScpiMessageProducer.DisableDioPorts().Data);
        }

        [Fact]
        public void DisableAllChannels_AnalogOnlyDevice_DoesNotSendDioCommand()
        {
            var device = CreateConnectedDevice(analogChannels: 4, digitalChannels: 0);
            device.EnableChannel(AnalogChannelAt(device, 0));
            device.SentMessages.Clear();

            device.DisableAllChannels();

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("0").Data, sent.Data);
        }

        [Fact]
        public void DisableAllChannels_DigitalOnlyDevice_DoesNotSendAdcCommand()
        {
            var device = CreateConnectedDevice(analogChannels: 0, digitalChannels: 4);
            device.EnableChannel(DigitalChannelAt(device, 0));
            device.SentMessages.Clear();

            device.DisableAllChannels();

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.DisableDioPorts().Data, sent.Data);
        }

        #endregion

        #region SetDioDirection

        [Fact]
        public void SetDioDirection_Output_SetsDirectionAndSendsCommand()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 2);

            device.SetDioDirection(channel, ChannelDirection.Output);

            Assert.Equal(ChannelDirection.Output, channel.Direction);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetDioPortDirection(2, 1).Data, sent.Data);
        }

        [Fact]
        public void SetDioDirection_Input_SendsZeroDirection()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 1);

            device.SetDioDirection(channel, ChannelDirection.Input);

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetDioPortDirection(1, 0).Data, sent.Data);
        }

        [Fact]
        public void SetDioDirection_OnAnalogChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);

            Assert.Throws<ArgumentException>(() => device.SetDioDirection(channel, ChannelDirection.Output));
        }

        [Fact]
        public void SetDioDirection_WithUnknownDirection_ThrowsArgumentOutOfRangeException()
        {
            var device = CreateConnectedDevice();
            var channel = DigitalChannelAt(device, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetDioDirection(channel, ChannelDirection.Unknown));
        }

        [Fact]
        public void SetDioDirection_WithForeignDigitalChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var foreign = new DigitalChannel(1); // correctly typed, but not a member of Channels

            Assert.Throws<ArgumentException>(() => device.SetDioDirection(foreign, ChannelDirection.Output));
        }

        #endregion

        #region SetDioValue

        [Fact]
        public void SetDioValue_High_SetsOutputValueAndSendsCommand()
        {
            var device = CreateConnectedDevice();
            var channel = (IDigitalChannel)DigitalChannelAt(device, 1);

            device.SetDioValue(channel, true);

            Assert.True(channel.OutputValue);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetDioPortState(1, 1).Data, sent.Data);
        }

        [Fact]
        public void SetDioValue_Low_SendsZeroState()
        {
            var device = CreateConnectedDevice();
            var channel = (IDigitalChannel)DigitalChannelAt(device, 3);
            channel.OutputValue = true;

            device.SetDioValue(channel, false);

            Assert.False(channel.OutputValue);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetDioPortState(3, 0).Data, sent.Data);
        }

        [Fact]
        public void SetDioValue_OnAnalogChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var channel = AnalogChannelAt(device, 0);

            Assert.Throws<ArgumentException>(() => device.SetDioValue(channel, true));
        }

        [Fact]
        public void SetDioValue_WithForeignDigitalChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var foreign = new DigitalChannel(1); // correctly typed, but not a member of Channels

            Assert.Throws<ArgumentException>(() => device.SetDioValue(foreign, true));
        }

        #endregion

        #region SetAnalogOutput

        [Fact]
        public void SetAnalogOutput_SendsLevelThenUpdate()
        {
            var device = CreateConnectedDevice();

            device.SetAnalogOutput(1, 5.0);

            Assert.Equal(2, device.SentMessages.Count);
            Assert.Equal(ScpiMessageProducer.SetAnalogOutputVoltage(1, 5.0).Data, device.SentMessages[0].Data);
            Assert.Equal(ScpiMessageProducer.UpdateDacOutputs.Data, device.SentMessages[1].Data);
        }

        [Fact]
        public void SetAnalogOutput_AddressesDacByChannelNumber()
        {
            var device = CreateConnectedDevice();
            // DAC channels are addressed by number and are not part of the populated input collection;
            // a channel number with no corresponding entry in Channels must still work and latch.
            device.SetAnalogOutput(7, 1.25);

            Assert.Equal(2, device.SentMessages.Count);
            Assert.Equal(ScpiMessageProducer.SetAnalogOutputVoltage(7, 1.25).Data, device.SentMessages[0].Data);
            Assert.Equal(ScpiMessageProducer.UpdateDacOutputs.Data, device.SentMessages[1].Data);
        }

        [Fact]
        public void SetAnalogOutput_NegativeChannel_ThrowsArgumentOutOfRangeException()
        {
            var device = CreateConnectedDevice();

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(-1, 1.0));
        }

        [Fact]
        public void SetAnalogOutput_NonFiniteVoltage_ThrowsArgumentOutOfRangeException()
        {
            var device = CreateConnectedDevice();

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(0, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(0, double.PositiveInfinity));
        }

        #endregion

        #region Membership and connection validation

        [Fact]
        public void EnableChannel_WithForeignChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var foreign = new AnalogChannel(0); // same number, but a different instance not in Channels

            Assert.Throws<ArgumentException>(() => device.EnableChannel(foreign));
        }

        [Fact]
        public void EnableChannel_WithNullChannel_ThrowsArgumentNullException()
        {
            var device = CreateConnectedDevice();

            Assert.Throws<ArgumentNullException>(() => device.EnableChannel(null!));
        }

        [Fact]
        public void DisableChannel_WithForeignChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice();
            var foreign = new AnalogChannel(0);

            Assert.Throws<ArgumentException>(() => device.DisableChannel(foreign));
        }

        [Fact]
        public void DisableChannel_WithNullChannel_ThrowsArgumentNullException()
        {
            var device = CreateConnectedDevice();

            Assert.Throws<ArgumentNullException>(() => device.DisableChannel(null!));
        }

        [Fact]
        public void ChannelManagement_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Populate channels but do not connect.
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.PopulateChannelsFromStatus(new DaqifiOutMessage
            {
                AnalogInPortNum = 2,
                AnalogInRes = 65535,
                DigitalPortNum = 2
            });
            var analog = AnalogChannelAt(device, 0);
            var digital = DigitalChannelAt(device, 0);

            Assert.Throws<InvalidOperationException>(() => device.EnableChannel(analog));
            Assert.Throws<InvalidOperationException>(() => device.EnableChannels(new[] { analog }));
            Assert.Throws<InvalidOperationException>(() => device.DisableChannel(analog));
            Assert.Throws<InvalidOperationException>(() => device.DisableAllChannels());
            Assert.Throws<InvalidOperationException>(() => device.SetDioDirection(digital, ChannelDirection.Output));
            Assert.Throws<InvalidOperationException>(() => device.SetDioValue(digital, true));
            Assert.Throws<InvalidOperationException>(() => device.SetAnalogOutput(0, 1.0));
            Assert.Throws<InvalidOperationException>(() => device.Reboot());
        }

        #endregion

        #region Reboot

        [Fact]
        public void Reboot_SendsRebootCommandAndDisconnects()
        {
            var device = CreateConnectedDevice();

            device.Reboot();

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.RebootDevice.Data, sent.Data);
            Assert.False(device.IsConnected);
        }

        #endregion

        /// <summary>
        /// An <see cref="IEnumerable{T}"/> that permits exactly one enumeration, throwing on any
        /// subsequent attempt. Used to prove EnableChannels materializes its input only once.
        /// </summary>
        private sealed class SingleEnumerationSequence : IEnumerable<IChannel>
        {
            private readonly IChannel[] _items;
            private bool _enumerated;

            public SingleEnumerationSequence(params IChannel[] items) => _items = items;

            public IEnumerator<IChannel> GetEnumerator()
            {
                if (_enumerated)
                {
                    throw new InvalidOperationException("Sequence was enumerated more than once.");
                }

                _enumerated = true;
                return ((IEnumerable<IChannel>)_items).GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
