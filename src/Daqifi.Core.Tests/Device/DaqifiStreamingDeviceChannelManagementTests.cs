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

        #region PWM

        // The device populates IsPwmCapable from the firmware board mask 0x00F9: channels
        // 0, 3, 4, 5, 6, 7 are capable; 1, 2 and 8+ are not. Use 8 digital channels so both
        // capable and non-capable channels exist.

        [Fact]
        public void PopulateChannels_SetsPwmCapabilityFromBoardMask()
        {
            var device = CreateConnectedDevice(digitalChannels: 16);

            var capable = device.Channels
                .OfType<IDigitalChannel>()
                .Where(c => c.IsPwmCapable)
                .Select(c => c.ChannelNumber)
                .OrderBy(n => n)
                .ToList();

            Assert.Equal(new[] { 0, 3, 4, 5, 6, 7 }, capable);
        }

        [Fact]
        public void SetPwmDutyCycle_OnCapableChannel_SetsBookkeepingAndSendsCommand()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = (IDigitalChannel)DigitalChannelAt(device, 4);

            device.SetPwmDutyCycle(channel, 50);

            Assert.Equal(50, channel.PwmDutyCyclePercent);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetPwmChannelDutyCycle(4, 50).Data, sent.Data);
        }

        [Theory]
        [InlineData(0)] // duty 0 is a firmware trap: stored but never applied; disable is the off switch
        [InlineData(101)]
        public void SetPwmDutyCycle_OutOfRange_ThrowsArgumentOutOfRangeException(int duty)
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = DigitalChannelAt(device, 4);

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetPwmDutyCycle(channel, duty));
        }

        [Fact]
        public void SetPwmDutyCycle_OnNonCapableChannel_ThrowsWithCapableList()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = DigitalChannelAt(device, 2);

            var ex = Assert.Throws<ArgumentException>(() => device.SetPwmDutyCycle(channel, 50));
            Assert.Contains("0, 3, 4, 5, 6, 7", ex.Message);
        }

        [Fact]
        public void SetPwmEnabled_True_SetsFlagAndSendsCommand()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = (IDigitalChannel)DigitalChannelAt(device, 4);

            device.SetPwmEnabled(channel, true);

            Assert.True(channel.IsPwmEnabled);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetPwmChannelEnabled(4, true).Data, sent.Data);
        }

        [Fact]
        public void SetPwmEnabled_True_OnNonCapableChannel_Throws()
        {
            // The firmware marks a channel PWM-active before its capability check fails and never
            // rolls it back (bricking digital writes on the channel), so the client hard-blocks this.
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = DigitalChannelAt(device, 2);

            var ex = Assert.Throws<ArgumentException>(() => device.SetPwmEnabled(channel, true));
            Assert.Contains("0, 3, 4, 5, 6, 7", ex.Message);
            Assert.Empty(device.SentMessages);
        }

        [Fact]
        public void SetPwmEnabled_False_OnNonCapableChannel_IsAllowedAsRecovery()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = DigitalChannelAt(device, 2);

            device.SetPwmEnabled(channel, false);

            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetPwmChannelEnabled(2, false).Data, sent.Data);
        }

        [Fact]
        public void SetPwmEnabled_False_ClearsOutputValueBookkeeping()
        {
            // Firmware zeroes the stored output value when PWM is disabled; local state mirrors it.
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = (IDigitalChannel)DigitalChannelAt(device, 4);
            channel.OutputValue = true;
            channel.IsPwmEnabled = true;

            device.SetPwmEnabled(channel, false);

            Assert.False(channel.IsPwmEnabled);
            Assert.False(channel.OutputValue);
        }

        [Fact]
        public void SetPwmEnabled_OnAnalogChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = AnalogChannelAt(device, 0);

            Assert.Throws<ArgumentException>(() => device.SetPwmEnabled(channel, true));
        }

        [Fact]
        public void SetPwmEnabled_WithForeignDigitalChannel_ThrowsArgumentException()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var foreign = new DigitalChannel(4, isPwmCapable: true);

            Assert.Throws<ArgumentException>(() => device.SetPwmEnabled(foreign, true));
        }

        [Fact]
        public void SetPwmFrequency_SendsCommandAddressedToChannelZeroAndTracksValue()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);

            device.SetPwmFrequency(1000);

            Assert.Equal(1000, device.PwmFrequencyHz);
            var sent = Assert.Single(device.SentMessages);
            Assert.Equal(ScpiMessageProducer.SetPwmChannelFrequency(0, 1000).Data, sent.Data);
        }

        [Theory]
        [InlineData(5)]     // 1-5 Hz silently wrap the firmware's 16-bit period register
        [InlineData(50001)] // above the advertised cap
        public void SetPwmFrequency_OutOfRange_ThrowsArgumentOutOfRangeException(int frequency)
        {
            var device = CreateConnectedDevice(digitalChannels: 8);

            Assert.Throws<ArgumentOutOfRangeException>(() => device.SetPwmFrequency(frequency));
        }

        [Fact]
        public void SetPwmEnabled_WhenDisconnected_ThrowsInvalidOperationException()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = DigitalChannelAt(device, 4);
            device.Disconnect();

            Assert.Throws<InvalidOperationException>(() => device.SetPwmEnabled(channel, true));
        }

        [Fact]
        public void PopulateChannelsFromStatus_PreservesPwmBookkeepingAcrossRefresh()
        {
            var device = CreateConnectedDevice(digitalChannels: 8);
            var channel = (IDigitalChannel)DigitalChannelAt(device, 4);
            device.SetPwmDutyCycle(channel, 42);
            device.SetPwmEnabled(channel, true);

            device.PopulateChannelsFromStatus(new DaqifiOutMessage
            {
                AnalogInPortNum = 4,
                AnalogInRes = 65535,
                DigitalPortNum = 8
            });

            var refreshed = (IDigitalChannel)DigitalChannelAt(device, 4);
            Assert.NotSame(channel, refreshed);
            Assert.True(refreshed.IsPwmEnabled);
            Assert.Equal(42, refreshed.PwmDutyCyclePercent);
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

        #region State preservation across status refresh

        [Fact]
        public void EnableState_SurvivesStatusRefresh_AndMaskStaysCorrect()
        {
            var device = CreateConnectedDevice(analogChannels: 4, digitalChannels: 4);
            device.EnableChannel(AnalogChannelAt(device, 0));
            device.EnableChannel(AnalogChannelAt(device, 1));
            device.SentMessages.Clear();

            // Simulate a later status refresh (e.g. reconnect / metadata re-query), which
            // recreates the channel instances. Previously-enabled channels must remain enabled.
            device.PopulateChannelsFromStatus(new DaqifiOutMessage
            {
                AnalogInPortNum = 4,
                AnalogInRes = 65535,
                DigitalPortNum = 4
            });

            Assert.True(AnalogChannelAt(device, 0).IsEnabled);
            Assert.True(AnalogChannelAt(device, 1).IsEnabled);

            // Enabling another channel still yields the full set-replace mask (0,1,2 => 1+2+4 = 7),
            // not a mask that dropped the channels enabled before the refresh.
            device.EnableChannel(AnalogChannelAt(device, 2));
            Assert.Equal(ScpiMessageProducer.EnableAdcChannels("7").Data, device.SentMessages.Last().Data);
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
