using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// IDigitalChannel.OutputValue is obsolete (#901); these tests keep covering it until it
// is removed in a major version.
#pragma warning disable CS0618

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests that drive <see cref="ChannelControlOperations"/> directly through its
/// <see cref="IDeviceOperationHost"/> seam, rather than through the
/// <see cref="DaqifiStreamingDevice"/> facade it was extracted from (#344, per the remaining
/// collaborators named in #464).
/// </summary>
/// <remarks>
/// <para>
/// The <c>DaqifiStreamingDevice*</c> test files already cover what each channel command puts on
/// the wire end-to-end through a testable device subclass, and are deliberately left untouched —
/// that is the evidence the extraction changed nothing observable. What a direct test adds is
/// everything that facade hides behind a single virtual <c>Send</c>: that validation runs, and in
/// what order, before any device state or channel mutation happens; that the ADC/DIO masks are
/// computed and sent only for channel types actually touched by a call; that the PWM
/// "already-sent" cache is consulted and only cleared by <see cref="ChannelControlOperations.ResetSentPwmFrequency"/>;
/// and that a channel not belonging to the device's snapshot is rejected before any command reaches
/// the host.
/// </para>
/// <para>
/// The fake host below throws <see cref="NotSupportedException"/> for every member outside this
/// collaborator's remit (the text-exchange engine, raw capture, streaming control, feature gates),
/// so a change that reaches for one of those fails loudly instead of passing quietly.
/// </para>
/// </remarks>
public class ChannelControlOperationsTests
{
    #region Construction

    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChannelControlOperations(null!));
    }

    #endregion

    #region EnableChannel / DisableChannel — single channel, ADC mask

    [Fact]
    public void EnableChannel_AnalogChannel_SendsAdcMaskForItAlone()
    {
        var channel = new AnalogChannel(channelNumber: 3, resolution: 65535);
        var host = new FakeHost(channel);
        var ops = new ChannelControlOperations(host);

        ops.EnableChannel(channel);

        Assert.True(channel.IsEnabled);
        Assert.Equal(new[] { "send:ENAble:VOLTage:DC 8" }, host.Calls); // 1u << 3 = 8
    }

    [Fact]
    public void DisableChannel_AnalogChannel_SendsUpdatedMask()
    {
        var enabled = new AnalogChannel(channelNumber: 0, resolution: 65535) { IsEnabled = true };
        var stillEnabled = new AnalogChannel(channelNumber: 1, resolution: 65535) { IsEnabled = true };
        var host = new FakeHost(enabled, stillEnabled);
        var ops = new ChannelControlOperations(host);

        ops.DisableChannel(enabled);

        Assert.False(enabled.IsEnabled);
        Assert.True(stillEnabled.IsEnabled);
        Assert.Equal(new[] { "send:ENAble:VOLTage:DC 2" }, host.Calls); // only channel 1 remains
    }

    [Fact]
    public void EnableChannel_NullChannel_Throws()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentNullException>(() => ops.EnableChannel(null!));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void EnableChannel_NotConnected_ThrowsAndSendsNothing()
    {
        var channel = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var host = new FakeHost(channel) { IsConnected = false };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<DeviceNotConnectedException>(() => ops.EnableChannel(channel));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void EnableChannel_ChannelNotBelongingToDevice_ThrowsAndSendsNothing()
    {
        var owned = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var stray = new AnalogChannel(channelNumber: 1, resolution: 65535);
        var host = new FakeHost(owned);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentException>(() => ops.EnableChannel(stray));
        Assert.Empty(host.Calls);
        Assert.False(stray.IsEnabled);
    }

    [Fact]
    public void EnableChannel_AnalogOutputChannel_Throws()
    {
        // Not an acquisition channel — enabling it would set a flag no command ever reads.
        var output = new AnalogOutputChannel(0);
        var host = new FakeHost(output);
        var ops = new ChannelControlOperations(host);

        var ex = Assert.Throws<ArgumentException>(() => ops.EnableChannel(output));
        Assert.Contains("not an acquisition channel", ex.Message);
        Assert.Empty(host.Calls);
    }

    #endregion

    #region EnableChannels — mixed analog/digital, digital-only send behavior

    [Fact]
    public void EnableChannels_MixedAnalogAndDigital_SendsOneCommandPerType()
    {
        var analog = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var digital = new DigitalChannel(channelNumber: 0);
        var host = new FakeHost(analog, digital);
        var ops = new ChannelControlOperations(host);

        ops.EnableChannels(new IChannel[] { analog, digital });

        Assert.True(analog.IsEnabled);
        Assert.True(digital.IsEnabled);
        Assert.Equal(
            new[] { "send:ENAble:VOLTage:DC 1", "send:DIO:PORt:ENAble 1" },
            host.Calls);
    }

    [Fact]
    public void EnableChannels_DigitalOnly_SendsOnlyDioCommand()
    {
        var digital = new DigitalChannel(channelNumber: 2);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.EnableChannels(new IChannel[] { digital });

        Assert.Equal(new[] { "send:DIO:PORt:ENAble 1" }, host.Calls);
    }

    [Fact]
    public void EnableChannels_NullCollection_Throws()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentNullException>(() => ops.EnableChannels(null!));
    }

    [Fact]
    public void EnableChannels_NullEntryInCollection_ThrowsBeforeMutatingAnything()
    {
        var real = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var host = new FakeHost(real);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentException>(() => ops.EnableChannels(new IChannel?[] { real, null }!));

        // Validation runs before mutation: the first (valid) entry must not have been enabled.
        Assert.False(real.IsEnabled);
        Assert.Empty(host.Calls);
    }

    #endregion

    #region DisableAllChannels

    [Fact]
    public void DisableAllChannels_ClearsEveryChannelAndSendsBothMasks()
    {
        var analog = new AnalogChannel(channelNumber: 4, resolution: 65535) { IsEnabled = true };
        var digital = new DigitalChannel(channelNumber: 0) { IsEnabled = true };
        var host = new FakeHost(analog, digital);
        var ops = new ChannelControlOperations(host);

        ops.DisableAllChannels();

        Assert.False(analog.IsEnabled);
        Assert.False(digital.IsEnabled);
        Assert.Equal(
            new[] { "send:ENAble:VOLTage:DC 0", "send:DIO:PORt:ENAble 0" },
            host.Calls);
    }

    [Fact]
    public void DisableAllChannels_AnalogOutputChannelPresent_IsIgnored()
    {
        // Neither an ADC-mask channel nor a DIO channel; must not affect either send.
        var output = new AnalogOutputChannel(0);
        var digital = new DigitalChannel(channelNumber: 0) { IsEnabled = true };
        var host = new FakeHost(output, digital);
        var ops = new ChannelControlOperations(host);

        ops.DisableAllChannels();

        Assert.Equal(new[] { "send:DIO:PORt:ENAble 0" }, host.Calls);
    }

    [Fact]
    public void DisableAllChannels_NotConnected_Throws()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<DeviceNotConnectedException>(() => ops.DisableAllChannels());
        Assert.Empty(host.Calls);
    }

    #endregion

    #region SetDioDirection / SetDioValue

    [Fact]
    public void SetDioDirection_ValidDigitalChannel_SendsAndMutates()
    {
        var digital = new DigitalChannel(channelNumber: 5);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.SetDioDirection(digital, ChannelDirection.Output);

        Assert.Equal(ChannelDirection.Output, digital.Direction);
        Assert.Equal(new[] { "send:DIO:PORt:DIRection 5,1" }, host.Calls);
    }

    [Fact]
    public void SetDioDirection_MutatesDirectionUnderTheChannelsLock()
    {
        // The status-frame resync writes this same property under the channels lock (#685), so
        // the command path has to write it under that lock too — the discipline SetChannelsEnabled
        // already follows for analog IsEnabled (#409). Qodo flagged the unsynchronized pair on
        // PR #686.
        var digital = new LockObservingDigitalChannel(channelNumber: 5);
        var host = new FakeHost(digital);
        digital.Host = host;
        var ops = new ChannelControlOperations(host);

        ops.SetDioDirection(digital, ChannelDirection.Output);

        Assert.True(digital.DirectionWrittenUnderLock);
        Assert.Equal(ChannelDirection.Output, digital.Direction);
        Assert.Equal(new[] { "send:DIO:PORt:DIRection 5,1" }, host.Calls);
    }

    [Fact]
    public void SetDioDirection_CommandWins_WhenAStatusFrameLandsWhileItIsInFlight()
    {
        // Qodo, PR #686. A status frame the device encoded before it applied this command can
        // land while the command is in flight and revert Core's view. Nothing would repair that:
        // this device sends no unsolicited status frames (bench-measured), so there may be no
        // later frame at all, and Core would report the pin backwards indefinitely while the
        // device sat in the commanded direction. The command has to have the last word.
        var digital = new DigitalChannel(channelNumber: 5) { Direction = ChannelDirection.Input };
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        // A stale status frame resyncing Direction back to Input, mid-command.
        host.OnSend = () => digital.Direction = ChannelDirection.Input;

        ops.SetDioDirection(digital, ChannelDirection.Output);

        Assert.Equal(ChannelDirection.Output, digital.Direction);
        Assert.Equal(new[] { "send:DIO:PORt:DIRection 5,1" }, host.Calls);
    }

    [Fact]
    public void SetDioDirection_FailedSend_LeavesCoreDirectionUntouched()
    {
        // Recording a direction the pin never took would tell every caller the pin is doing
        // something it is not — the same principle as the PWM guard (#449).
        var digital = new DigitalChannel(channelNumber: 5) { Direction = ChannelDirection.Input };
        var host = new FakeHost(digital) { SendFailure = new IOException("cable pulled") };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<IOException>(() => ops.SetDioDirection(digital, ChannelDirection.Output));

        Assert.Equal(ChannelDirection.Input, digital.Direction);
    }

    [Fact]
    public void SetDioDirection_AnalogChannel_ThrowsBeforeConnectionCheck()
    {
        var analog = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var host = new FakeHost(analog) { IsConnected = false };
        var ops = new ChannelControlOperations(host);

        // Type validation precedes the connection check, so this must be an ArgumentException,
        // not DeviceNotConnectedException, even though the host reports disconnected.
        Assert.Throws<ArgumentException>(() => ops.SetDioDirection(analog, ChannelDirection.Output));
    }

    [Theory]
    [InlineData(ChannelDirection.Unknown)]
    public void SetDioDirection_InvalidDirection_Throws(ChannelDirection direction)
    {
        var digital = new DigitalChannel(channelNumber: 0);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentOutOfRangeException>(() => ops.SetDioDirection(digital, direction));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetDioDirection_PwmEnabledChannel_Throws()
    {
        var digital = new DigitalChannel(channelNumber: 0, isPwmCapable: true) { IsPwmEnabled = true };
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<InvalidOperationException>(() => ops.SetDioDirection(digital, ChannelDirection.Output));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetDioValue_ValidDigitalChannel_SendsAndMirrorsOutputValue()
    {
        var digital = new DigitalChannel(channelNumber: 2);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.SetDioValue(digital, true);

        Assert.True(digital.OutputValue);
        Assert.Equal(new[] { "send:DIO:PORt:STATe 2,1" }, host.Calls);
    }

    [Fact]
    public void SetDioValue_PwmEnabledChannel_Throws()
    {
        var digital = new DigitalChannel(channelNumber: 0, isPwmCapable: true) { IsPwmEnabled = true };
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<InvalidOperationException>(() => ops.SetDioValue(digital, true));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region PWM enable / duty cycle

    [Fact]
    public void SetPwmEnabled_PwmCapableChannel_SendsAndMutates()
    {
        var digital = new DigitalChannel(channelNumber: 4, isPwmCapable: true);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.SetPwmEnabled(digital, true);

        Assert.True(digital.IsPwmEnabled);
        Assert.Equal(new[] { "send:PWM:CHannel:ENable 4,1" }, host.Calls);
    }

    [Fact]
    public void SetPwmEnabled_Disabling_ZeroesOutputValueButKeepsDirection()
    {
        var digital = new DigitalChannel(channelNumber: 0, isPwmCapable: true)
        {
            IsPwmEnabled = true,
            OutputValue = true,
            Direction = ChannelDirection.Output
        };
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.SetPwmEnabled(digital, false);

        Assert.False(digital.IsPwmEnabled);
        Assert.False(digital.OutputValue);
        Assert.Equal(ChannelDirection.Output, digital.Direction);
    }

    [Fact]
    public void SetPwmEnabled_NonPwmCapableChannel_EnablingThrowsWithCapableChannelList()
    {
        var incapable = new DigitalChannel(channelNumber: 0, isPwmCapable: false);
        var capable = new DigitalChannel(channelNumber: 3, isPwmCapable: true);
        var host = new FakeHost(incapable, capable);
        var ops = new ChannelControlOperations(host);

        var ex = Assert.Throws<ArgumentException>(() => ops.SetPwmEnabled(incapable, true));
        Assert.Contains("3", ex.Message);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmEnabled_NonPwmCapableChannel_DisablingIsAllowed()
    {
        // Disabling is the only recovery from a stuck-PWM-active firmware state, so it must be
        // accepted even on a channel this model doesn't think is PWM-capable.
        var incapable = new DigitalChannel(channelNumber: 1, isPwmCapable: false);
        var host = new FakeHost(incapable);
        var ops = new ChannelControlOperations(host);

        ops.SetPwmEnabled(incapable, false);

        Assert.Equal(new[] { "send:PWM:CHannel:ENable 1,0" }, host.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetPwmEnabled_FailedSend_LeavesTheChannelAsItWas(bool enabled)
    {
        // The command never reached the device, so the pin is where it was. A channel left
        // marked PWM here would make Core refuse every digital command for it (#945).
        var digital = new DigitalChannel(channelNumber: 4, isPwmCapable: true);
        digital.ApplyDirection(ChannelDirection.Output);
        digital.ApplyOutputValue(true);
        if (!enabled)
        {
            digital.EnterPwm();
        }

        var modeBefore = digital.Mode;
        var host = new FakeHost(digital) { SendFailure = new IOException("cable pulled") };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<IOException>(() => ops.SetPwmEnabled(digital, enabled));

        Assert.Equal(modeBefore, digital.Mode);
        Assert.Equal(!enabled, digital.IsPwmEnabled);
        Assert.Equal(ChannelDirection.Output, digital.Direction);
        Assert.True(digital.OutputValue);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmEnabled_FailedSend_DoesNotBlockTheNextDigitalCommand()
    {
        var digital = new DigitalChannel(channelNumber: 4, isPwmCapable: true);
        var host = new FakeHost(digital) { SendFailure = new IOException("cable pulled") };
        var ops = new ChannelControlOperations(host);
        Assert.Throws<IOException>(() => ops.SetPwmEnabled(digital, true));

        host.SendFailure = null;
        ops.SetDioValue(digital, true);

        Assert.Equal(new[] { "send:DIO:PORt:STATe 4,1" }, host.Calls);
    }

    [Fact]
    public void SetDioValue_FailedSend_LeavesTheChannelAsItWas()
    {
        var digital = new DigitalChannel(channelNumber: 2);
        digital.ApplyDirection(ChannelDirection.Output);
        var host = new FakeHost(digital) { SendFailure = new IOException("cable pulled") };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<IOException>(() => ops.SetDioValue(digital, true));

        Assert.False(digital.OutputValue);
        Assert.Equal(DigitalPinMode.Output, digital.Mode);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmDutyCycle_FailedSend_LeavesTheChannelAsItWas()
    {
        var digital = new DigitalChannel(channelNumber: 1, isPwmCapable: true) { PwmDutyCyclePercent = 30 };
        var host = new FakeHost(digital) { SendFailure = new IOException("cable pulled") };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<IOException>(() => ops.SetPwmDutyCycle(digital, 75));

        Assert.Equal(30, digital.PwmDutyCyclePercent);
        Assert.Equal(DigitalPinMode.Input, digital.Mode);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmEnabled_LinkDropsWhileTheEnableIsInFlight_DoesNotUndoTheReset()
    {
        // The send returns, the link drops and the status handler resets PWM, and only then
        // does the enable reach its record step. Recording it would leave the channel in PWM
        // mode on a disconnected device, and across the reconnect (#945).
        var digital = new DigitalChannel(channelNumber: 4, isPwmCapable: true);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);
        host.OnSend = () =>
        {
            host.IsConnected = false;
            ops.ResetPwmEnabled();
        };

        ops.SetPwmEnabled(digital, true);

        Assert.False(digital.IsPwmEnabled);
        Assert.Equal(DigitalPinMode.Input, digital.Mode);
    }

    [Fact]
    public void ResetPwmEnabled_TakesEveryChannelOutOfPwmModeAndKeepsItsDirection()
    {
        var pwmOverOutput = new DigitalChannel(channelNumber: 0, isPwmCapable: true);
        pwmOverOutput.ApplyDirection(ChannelDirection.Output);
        pwmOverOutput.EnterPwm();
        var pwmOverInput = new DigitalChannel(channelNumber: 3, isPwmCapable: true);
        pwmOverInput.EnterPwm();
        var plainOutput = new DigitalChannel(channelNumber: 1);
        plainOutput.ApplyDirection(ChannelDirection.Output);
        plainOutput.ApplyOutputValue(true);
        var host = new FakeHost(pwmOverOutput, pwmOverInput, plainOutput);
        var ops = new ChannelControlOperations(host);

        ops.ResetPwmEnabled();

        Assert.Equal(DigitalPinMode.Output, pwmOverOutput.Mode);
        Assert.Equal(DigitalPinMode.Input, pwmOverInput.Mode);
        // A channel that was not in PWM mode is not touched.
        Assert.Equal(DigitalPinMode.Output, plainOutput.Mode);
        Assert.True(plainOutput.OutputValue);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmEnabled_AnalogChannel_Throws()
    {
        var analog = new AnalogChannel(channelNumber: 0, resolution: 65535);
        var host = new FakeHost(analog);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentException>(() => ops.SetPwmEnabled(analog, true));
    }

    [Fact]
    public void SetPwmDutyCycle_ValidRange_SendsAndMutates()
    {
        var digital = new DigitalChannel(channelNumber: 1, isPwmCapable: true);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        ops.SetPwmDutyCycle(digital, 75);

        Assert.Equal(75, digital.PwmDutyCyclePercent);
        Assert.Equal(new[] { "send:PWM:CHannel:DUTY 1,75" }, host.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void SetPwmDutyCycle_OutOfRange_Throws(int dutyCycle)
    {
        var digital = new DigitalChannel(channelNumber: 0, isPwmCapable: true);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentOutOfRangeException>(() => ops.SetPwmDutyCycle(digital, dutyCycle));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SetPwmDutyCycle_NonPwmCapableChannel_Throws()
    {
        var digital = new DigitalChannel(channelNumber: 0, isPwmCapable: false);
        var host = new FakeHost(digital);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentException>(() => ops.SetPwmDutyCycle(digital, 50));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region PWM frequency — the skip-if-unchanged cache

    [Fact]
    public void SetPwmFrequency_FirstCall_Sends()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        ops.SetPwmFrequency(2000);

        Assert.Equal(2000, ops.PwmFrequencyHz);
        Assert.Equal(new[] { "send:PWM:CHannel:FREQuency 0,2000" }, host.Calls);
    }

    [Fact]
    public void SetPwmFrequency_SameValueTwice_SecondCallSkipsTheSend()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        ops.SetPwmFrequency(1500);
        ops.SetPwmFrequency(1500);

        Assert.Single(host.Calls);
    }

    [Fact]
    public void SetPwmFrequency_AfterResetSentPwmFrequency_SendsAgainEvenIfUnchanged()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        ops.SetPwmFrequency(1500);
        ops.ResetSentPwmFrequency();
        ops.SetPwmFrequency(1500);

        Assert.Equal(2, host.Calls.Count);
    }

    [Fact]
    public void SetPwmFrequency_DifferentValue_SendsAgain()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        ops.SetPwmFrequency(1000);
        ops.SetPwmFrequency(2000);

        Assert.Equal(
            new[]
            {
                "send:PWM:CHannel:FREQuency 0,1000",
                "send:PWM:CHannel:FREQuency 0,2000"
            },
            host.Calls);
    }

    [Fact]
    public void SetPwmFrequency_OutOfRange_ThrowsWithoutTouchingConnectionOrCache()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ops.SetPwmFrequency(DaqifiStreamingDevice.MinPwmFrequencyHz - 1));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region PWM frequency — the floor the device advertises (#958)

    /// <summary>
    /// A host whose capability document puts <paramref name="floorHz"/> on every PWM-capable pin
    /// (0, 3, 4, 5, 6 and 7), the shape firmware v3.9.0 emits, with the
    /// <see cref="DeviceFeature.PwmFrequencyFloor"/> gate open or shut.
    /// </summary>
    private static FakeHost HostAdvertisingPwmFloor(int? floorHz, bool floorTrusted)
    {
        var host = new FakeHost();
        if (floorTrusted)
        {
            host.SupportedFeatures.Add(DeviceFeature.PwmFrequencyFloor);
        }

        host.Metadata.ApplyCapabilityDocument(DocumentWithPwmFloors(
            new[] { 0, 3, 4, 5, 6, 7 }.Select(id => (id, floorHz)).ToArray()));
        return host;
    }

    private static CapabilityDocument DocumentWithPwmFloors(params (int Id, int? FloorHz)[] pins) =>
        new()
        {
            SchemaVersion = 2,
            Channels = pins
                .Select(pin => new CapabilityChannel
                {
                    Id = pin.Id,
                    Kind = CapabilityChannelKind.DigitalIo,
                    SupportsPwm = true,
                    PwmMinimumFrequencyHz = pin.FloorHz,
                    PwmMaximumFrequencyHz = DaqifiStreamingDevice.MaxPwmFrequencyHz
                })
                .ToArray()
        };

    [Theory]
    [InlineData(8)] // a unit whose clock puts the floor above the fixed 6 Hz
    [InlineData(4)] // and one whose clock puts it below
    public void SetPwmFrequency_TrustedAdvertisedFloor_IsTheFloorEnforced(int floorHz)
    {
        var host = HostAdvertisingPwmFloor(floorHz, floorTrusted: true);
        var ops = new ChannelControlOperations(host);

        Assert.Equal(floorHz, ops.PwmFrequencyFloorHz);

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => ops.SetPwmFrequency(floorHz - 1));
        Assert.Contains($"{floorHz}-{DaqifiStreamingDevice.MaxPwmFrequencyHz} Hz", refused.Message);
        Assert.Empty(host.Calls);

        ops.SetPwmFrequency(floorHz);

        Assert.Equal(new[] { $"send:PWM:CHannel:FREQuency 0,{floorHz}" }, host.Calls);
    }

    [Fact]
    public void SetPwmFrequency_FloorNotTrusted_KeepsTheFixedFloorDespiteTheDocument()
    {
        // Firmware before v3.9.0 advertises 1 Hz on every PWM pin; believing it would let a
        // frequency through that the 16-bit timer wraps into the kilohertz range.
        var host = HostAdvertisingPwmFloor(1, floorTrusted: false);
        var ops = new ChannelControlOperations(host);

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, ops.PwmFrequencyFloorHz);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ops.SetPwmFrequency(DaqifiStreamingDevice.MinPwmFrequencyHz - 1));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void PwmFrequencyFloorHz_TrustedButNoDocument_IsTheFixedFloor()
    {
        var host = new FakeHost();
        host.SupportedFeatures.Add(DeviceFeature.PwmFrequencyFloor);
        var ops = new ChannelControlOperations(host);

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, ops.PwmFrequencyFloorHz);
    }

    [Fact]
    public void PwmFrequencyFloorHz_TrustedDocumentWithNoFloorStated_IsTheFixedFloor()
    {
        var host = HostAdvertisingPwmFloor(null, floorTrusted: true);
        var ops = new ChannelControlOperations(host);

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, ops.PwmFrequencyFloorHz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DaqifiStreamingDevice.MaxPwmFrequencyHz + 1)]
    public void PwmFrequencyFloorHz_AdvertisedValueCoreCannotCommand_IsIgnored(int advertised)
    {
        var host = HostAdvertisingPwmFloor(advertised, floorTrusted: true);
        var ops = new ChannelControlOperations(host);

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, ops.PwmFrequencyFloorHz);
    }

    [Fact]
    public void PwmFrequencyFloorHz_PinsDisagree_TakesTheHighestFloor()
    {
        // The firmware states one device-wide floor today. Should two pins ever differ, only the
        // higher one is accepted by both.
        var host = new FakeHost();
        host.SupportedFeatures.Add(DeviceFeature.PwmFrequencyFloor);
        host.Metadata.ApplyCapabilityDocument(DocumentWithPwmFloors((0, 5), (3, 9), (4, 7)));
        var ops = new ChannelControlOperations(host);

        Assert.Equal(9, ops.PwmFrequencyFloorHz);
    }

    [Fact]
    public void PwmFrequencyFloorHz_IgnoresFloorsOnPinsWithoutPwm()
    {
        var host = new FakeHost();
        host.SupportedFeatures.Add(DeviceFeature.PwmFrequencyFloor);
        host.Metadata.ApplyCapabilityDocument(new CapabilityDocument
        {
            SchemaVersion = 2,
            Channels = new[]
            {
                new CapabilityChannel
                {
                    Id = 0, Kind = CapabilityChannelKind.DigitalIo, SupportsPwm = true,
                    PwmMinimumFrequencyHz = 7
                },
                // A stray floor on a pin that is not PWM-capable must not raise the device's.
                new CapabilityChannel
                {
                    Id = 1, Kind = CapabilityChannelKind.DigitalIo, SupportsPwm = false,
                    PwmMinimumFrequencyHz = 40
                }
            }
        });
        var ops = new ChannelControlOperations(host);

        Assert.Equal(7, ops.PwmFrequencyFloorHz);
    }

    #endregion

    #region Analog output — staging, latching, readback

    [Fact]
    public void SetAnalogOutput_InRange_StagesThenLatches()
    {
        var output = new AnalogOutputChannel(0, minimumVoltage: 0.0, maximumVoltage: 5.0);
        var host = new FakeHost(output);
        var ops = new ChannelControlOperations(host);

        ops.SetAnalogOutput(0, 3.3);

        Assert.Equal(
            new[] { "send:SOURce:VOLTage:LEVel 0,3.3", "send:CONFigure:DAC:UPDATE" },
            host.Calls);
        Assert.Equal(3.3, output.OutputVoltage);
    }

    [Fact]
    public void StageAnalogOutput_OutOfRange_ThrowsAndDoesNotSend()
    {
        var output = new AnalogOutputChannel(0, minimumVoltage: 0.0, maximumVoltage: 5.0);
        var host = new FakeHost(output);
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentOutOfRangeException>(() => ops.StageAnalogOutput(0, 10.0));
        Assert.Empty(host.Calls);
        Assert.Null(output.PendingVoltage);
    }

    [Fact]
    public void StageAnalogOutput_NegativeChannelNumber_Throws()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        Assert.Throws<ArgumentOutOfRangeException>(() => ops.StageAnalogOutput(-1, 1.0));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void StageAnalogOutput_UnmodelledChannelNumber_SendsByNumberAnyway()
    {
        // No AnalogOutputChannel with this number in the snapshot: the command still goes out
        // by number, matching the only analog-output path Core has ever had.
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);

        ops.StageAnalogOutput(7, 2.5);

        Assert.Equal(new[] { "send:SOURce:VOLTage:LEVel 7,2.5" }, host.Calls);
    }

    [Fact]
    public void LatchAnalogOutputs_LatchesEveryStagedChannel()
    {
        var a = new AnalogOutputChannel(0, minimumVoltage: 0.0, maximumVoltage: 5.0);
        var b = new AnalogOutputChannel(1, minimumVoltage: 0.0, maximumVoltage: 5.0);
        var host = new FakeHost(a, b);
        var ops = new ChannelControlOperations(host);

        ops.StageAnalogOutput(0, 1.0);
        ops.StageAnalogOutput(1, 2.0);
        ops.LatchAnalogOutputs();

        Assert.Equal(1.0, a.OutputVoltage);
        Assert.Equal(2.0, b.OutputVoltage);
        Assert.Null(a.PendingVoltage);
        Assert.Null(b.PendingVoltage);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_ParsesVoltageAndUpdatesModelledChannel()
    {
        var output = new AnalogOutputChannel(2, minimumVoltage: 0.0, maximumVoltage: 5.0);
        var host = new FakeHost(output);
        host.EnqueueResponse("3.30");
        var ops = new ChannelControlOperations(host);

        var volts = await ops.GetAnalogOutputAsync(2);

        Assert.Equal(3.30, volts);
        Assert.Equal(3.30, output.OutputVoltage);
        Assert.Equal(new[] { "send:SOURce:VOLTage:LEVel? 2" }, host.Calls);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_ScpiError_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("-113,\"Undefined header\"");
        var ops = new ChannelControlOperations(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.GetAnalogOutputAsync(0));
    }

    [Fact]
    public async Task GetAnalogOutputAsync_NonNumericResponse_ThrowsDescribingWhatCameBack()
    {
        var host = new FakeHost();
        host.EnqueueResponse("not-a-voltage");
        var ops = new ChannelControlOperations(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.GetAnalogOutputAsync(0));
        Assert.Contains("not-a-voltage", ex.Message);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_AlreadyCancelled_ThrowsWithoutSending()
    {
        var host = new FakeHost();
        var ops = new ChannelControlOperations(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ops.GetAnalogOutputAsync(0, cts.Token));

        Assert.Empty(host.Calls);
    }

    #endregion

    /// <summary>
    /// An <see cref="IDigitalChannel"/> that records whether its <see cref="Direction"/> was
    /// assigned from inside <see cref="FakeHost.WithChannelsLock"/>. A wrapper rather than a
    /// subclass because <see cref="DigitalChannel.Direction"/> is not virtual; every other member
    /// forwards to a real <see cref="DigitalChannel"/> so the behaviour under test is unchanged.
    /// </summary>
    private sealed class LockObservingDigitalChannel : IDigitalChannel
    {
        private readonly DigitalChannel _inner;

        public LockObservingDigitalChannel(int channelNumber)
        {
            _inner = new DigitalChannel(channelNumber);
        }

        public FakeHost? Host { get; set; }

        public bool DirectionWrittenUnderLock { get; private set; }

        public ChannelDirection Direction
        {
            get => _inner.Direction;
            set
            {
                DirectionWrittenUnderLock = Host?.InChannelsLock ?? false;
                _inner.Direction = value;
            }
        }

        public int ChannelNumber => _inner.ChannelNumber;
        public string Name { get => _inner.Name; set => _inner.Name = value; }
        public bool IsEnabled { get => _inner.IsEnabled; set => _inner.IsEnabled = value; }
        public ChannelType Type => _inner.Type;
        public IDataSample? ActiveSample => _inner.ActiveSample;
        public bool OutputValue { get => _inner.OutputValue; set => _inner.OutputValue = value; }
        public bool IsHigh => _inner.IsHigh;
        public bool IsPwmCapable => _inner.IsPwmCapable;
        public bool IsPwmEnabled { get => _inner.IsPwmEnabled; set => _inner.IsPwmEnabled = value; }
        public int PwmDutyCyclePercent { get => _inner.PwmDutyCyclePercent; set => _inner.PwmDutyCyclePercent = value; }

        public event EventHandler<SampleReceivedEventArgs>? SampleReceived
        {
            add => _inner.SampleReceived += value;
            remove => _inner.SampleReceived -= value;
        }

        public void SetActiveSample(double value, DateTime timestamp) => _inner.SetActiveSample(value, timestamp);

        public void SetActiveSample(IDataSample sample) => _inner.SetActiveSample(sample);
    }

    #region Fake host

    /// <summary>
    /// Minimal <see cref="IDeviceOperationHost"/> double scoped to what
    /// <see cref="ChannelControlOperations"/> actually uses: channel snapshot/lock, connection
    /// state, <see cref="Send{T}"/>, one text exchange for the analog-output readback, and the
    /// feature gate and capability document behind the PWM frequency floor. Every other member
    /// throws <see cref="NotSupportedException"/> so a change that reaches outside this
    /// collaborator's remit fails loudly.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<string> _calls = new();
        private readonly List<IChannel> _channels;
        private readonly Queue<IReadOnlyList<string>> _responses = new();
        private readonly object _channelsLockObj = new();

        /// <summary>
        /// True while a <see cref="WithChannelsLock"/> callback is on the stack. Lets
        /// <see cref="Send{T}"/> catch the exact regression <see cref="IDeviceOperationHost.WithChannelsLock"/>
        /// forbids — blocking I/O issued from inside the critical section — instead of silently
        /// passing it.
        /// </summary>
        private bool _inChannelsLock;

        /// <summary>
        /// Whether a <see cref="WithChannelsLock"/> callback is currently on the stack, so a test
        /// can assert that a mutation happens inside the critical section rather than beside it.
        /// </summary>
        public bool InChannelsLock => _inChannelsLock;

        public FakeHost(params IChannel[] channels)
        {
            _channels = channels.ToList();
        }

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) { return _calls.ToArray(); } }
        }

        public bool IsConnected { get; set; } = true;
        public bool IsStreaming { get; set; }
        public void EndStreamingSession() => IsStreaming = false;
        public void MarkStreamingStartRefused() => IsStreaming = false;
        public int StreamingFrequency { get; set; } = 100;
        public long ChannelStateVersion { get; set; }

        public void EnqueueResponse(params string[] lines) => _responses.Enqueue(lines);

        private void Record(string call)
        {
            lock (_calls) { _calls.Add(call); }
        }

        /// <summary>
        /// Runs at the moment of a <see cref="Send{T}"/>, so a test can simulate something landing
        /// while a command is in flight — a status frame resyncing channel state, say.
        /// </summary>
        public Action? OnSend { get; set; }

        /// <summary>
        /// When set, <see cref="Send{T}"/> throws this instead of recording, so a test can check
        /// what local state a failed command leaves behind.
        /// </summary>
        public Exception? SendFailure { get; set; }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (_inChannelsLock)
            {
                throw new InvalidOperationException(
                    "Send was called from inside WithChannelsLock — blocking I/O must stay outside " +
                    "the channels lock (see IDeviceOperationHost.WithChannelsLock).");
            }

            if (SendFailure is not null)
            {
                throw SendFailure;
            }

            Record("send:" + message.Data);
            OnSend?.Invoke();
        }

        public IReadOnlyList<IChannel> SnapshotChannels() => _channels.ToArray();

        public void WithChannelsLock(Action action)
        {
            lock (_channelsLockObj)
            {
                _inChannelsLock = true;
                try
                {
                    action();
                }
                finally
                {
                    _inChannelsLock = false;
                }
            }
        }

        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            setupAction();
            var lines = _responses.Count > 0 ? _responses.Dequeue() : Array.Empty<string>();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        /// <summary>
        /// The features <see cref="Supports"/> reports. Empty by default, the same answer a device
        /// with no reported firmware version gets, so the PWM floor stays the fixed constant
        /// unless a test opts in.
        /// </summary>
        public HashSet<DeviceFeature> SupportedFeatures { get; } = new();

        public bool Supports(DeviceFeature feature)
        {
            // The PWM floor is the only branch this collaborator takes on a feature.
            Assert.Equal(DeviceFeature.PwmFrequencyFloor, feature);
            return SupportedFeatures.Contains(feature);
        }

        public void EnsureSupported(DeviceFeature feature)
            => Assert.Equal(DeviceFeature.AnalogOutput, feature);

        /// <summary>
        /// Real metadata, read only for the capability document that carries the PWM floor.
        /// </summary>
        public DeviceMetadata Metadata { get; } = new();

        // Outside this collaborator's remit — reaching for any of these is a regression.
        public void StopStreaming() => throw new NotSupportedException();
        public void StartStreaming() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<System.IO.Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }

    #endregion
}
