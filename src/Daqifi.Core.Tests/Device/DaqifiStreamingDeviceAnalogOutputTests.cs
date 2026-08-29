using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Covers the analog-output (DAC) channel model end to end on a device: where the channels come
/// from, that a status message cannot wipe them, the range check that runs before anything
/// reaches the wire, the stage/latch pair, and the readback.
/// </summary>
public class DaqifiStreamingDeviceAnalogOutputTests
{
    /// <summary>
    /// Two DAC channels shaped exactly as the firmware emits them (see
    /// <c>EmitAoutChannelJson</c> in SCPIInterface.c), alongside one analog input so the tests
    /// can show outputs coexisting with the acquisition channels.
    /// </summary>
    private const string TwoDacDocument = """
        {"schema_version":2,"channels":[
          {"id":0,"kind":"analog-input","signal_type":"voltage","unit":"V","resolution_bits":12,"simultaneous":false,"differential":false,"ranges":[{"min":0.000,"max":5.000}],"extensions":{}},
          {"id":0,"kind":"analog-output","signal_type":"voltage","unit":"V","resolution_bits":12,"ranges":[{"min":0.000,"max":10.000}],"calibration":{"model":"linear","user_override_supported":true},"extensions":{}},
          {"id":1,"kind":"analog-output","signal_type":"voltage","unit":"V","resolution_bits":12,"ranges":[{"min":-5.000,"max":5.000}],"calibration":{"model":"linear","user_override_supported":true},"extensions":{}}
        ]}
        """;

    #region Population from the capability document

    [Fact]
    public void Sync_DocumentWithDacChannels_PutsThemInTheChannelSnapshot()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);

        var count = device.SyncAnalogOutputChannelsFromCapabilities();

        Assert.Equal(2, count);
        var outputs = OutputChannels(device);
        Assert.Equal(new[] { 0, 1 }, outputs.Select(c => c.ChannelNumber));
        Assert.All(outputs, c => Assert.Equal(ChannelType.AnalogOutput, c.Type));
        Assert.All(outputs, c => Assert.Equal(ChannelDirection.Output, c.Direction));
        Assert.All(outputs, c => Assert.Equal(12, c.ResolutionBits));
        Assert.All(outputs, c => Assert.False(c.RangeIsAssumed));
        Assert.Equal(0.0, outputs[0].MinimumVoltage);
        Assert.Equal(10.0, outputs[0].MaximumVoltage);
        Assert.Equal(-5.0, outputs[1].MinimumVoltage);
        Assert.Equal(5.0, outputs[1].MaximumVoltage);
    }

    [Fact]
    public void Sync_DocumentWithoutDacChannels_AddsNothing()
    {
        var device = ConnectedDeviceWith("""
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-input","signal_type":"voltage","unit":"V","resolution_bits":12,"ranges":[{"min":0.000,"max":5.000}],"extensions":{}}
            ]}
            """);

        Assert.Equal(0, device.SyncAnalogOutputChannelsFromCapabilities());
        Assert.Empty(OutputChannels(device));
    }

    [Fact]
    public void Sync_NoDocumentRead_AddsNothing()
    {
        var device = new CapturingAnalogOutputDevice();
        device.Connect();

        Assert.Equal(0, device.SyncAnalogOutputChannelsFromCapabilities());
        Assert.Empty(OutputChannels(device));
    }

    [Fact]
    public void Sync_Twice_KeepsTheSameChannelInstances()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        var first = OutputChannels(device);

        device.SyncAnalogOutputChannelsFromCapabilities();
        var second = OutputChannels(device);

        Assert.Equal(2, second.Count);
        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
    }

    [Fact]
    public void Sync_RangeMissingFromTheDocument_FallsBackAndSaysSo()
    {
        var device = ConnectedDeviceWith("""
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-output","signal_type":"voltage","unit":"V","resolution_bits":12,"extensions":{}}
            ]}
            """);

        device.SyncAnalogOutputChannelsFromCapabilities();

        var output = Assert.Single(OutputChannels(device));
        Assert.True(output.RangeIsAssumed);
        Assert.Equal(AnalogOutputChannel.DefaultMinimumVoltage, output.MinimumVoltage);
        Assert.Equal(AnalogOutputChannel.DefaultMaximumVoltage, output.MaximumVoltage);
    }

    [Fact]
    public void Sync_ImplausibleResolution_FallsBackToTheNq3Default()
    {
        var device = ConnectedDeviceWith("""
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-output","signal_type":"voltage","unit":"V","resolution_bits":0,"ranges":[{"min":0.000,"max":10.000}],"extensions":{}}
            ]}
            """);

        device.SyncAnalogOutputChannelsFromCapabilities();

        var output = Assert.Single(OutputChannels(device));
        Assert.Equal(AnalogOutputChannel.DefaultResolutionBits, output.ResolutionBits);
        // A bad resolution says nothing about the range, which was stated and stays stated.
        Assert.False(output.RangeIsAssumed);
    }

    [Fact]
    public void Sync_DuplicateChannelId_ModelsItOnce()
    {
        var device = ConnectedDeviceWith("""
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-output","resolution_bits":12,"ranges":[{"min":0.000,"max":10.000}],"extensions":{}},
              {"id":0,"kind":"analog-output","resolution_bits":12,"ranges":[{"min":-5.000,"max":5.000}],"extensions":{}}
            ]}
            """);

        Assert.Equal(1, device.SyncAnalogOutputChannelsFromCapabilities());
        var output = Assert.Single(OutputChannels(device));
        Assert.Equal(10.0, output.MaximumVoltage); // the first description wins
    }

    [Fact]
    public void PopulateChannelsFromStatus_AfterSync_KeepsTheOutputChannels()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        var beforeRepopulation = OutputChannels(device);

        // A status frame describes analog inputs and DIO only; the firmware never fills in the
        // protobuf's DAC fields, so this must not be read as "the device has no outputs".
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095,
            DigitalPortNum = 2,
        });

        var afterRepopulation = OutputChannels(device);
        Assert.Equal(2, afterRepopulation.Count);
        Assert.Same(beforeRepopulation[0], afterRepopulation[0]);
        Assert.Same(beforeRepopulation[1], afterRepopulation[1]);
        Assert.Equal(2, device.GetChannelsSnapshot().Count(c => c.Type == ChannelType.Analog));
        Assert.Equal(2, device.GetChannelsSnapshot().Count(c => c.Type == ChannelType.Digital));
    }

    [Fact]
    public void PopulateChannelsFromStatus_AfterTheDeviceMovedToAnotherBoard_DropsTheOutputChannels()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        Assert.Equal(2, OutputChannels(device).Count);

        // Reconnecting this instance to a different known board drops the capability
        // document it read from the previous one — and the DAC channels that document
        // described must not linger as another board's outputs.
        device.Metadata.UpdateFromProtobuf(new DaqifiOutMessage { DevicePn = "Nq3" });
        device.Metadata.UpdateFromProtobuf(new DaqifiOutMessage { DevicePn = "Nq1" });
        Assert.Null(device.Metadata.CapabilityDocument);

        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095,
        });

        Assert.Empty(OutputChannels(device));
    }

    [Fact]
    public void PopulateChannelsFromStatus_WhenTheBoardIsUnchanged_KeepsTheOutputChannels()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        // The same board repeating its part number is not a board change, so the document —
        // and the channels it described — survive.
        device.Metadata.UpdateFromProtobuf(new DaqifiOutMessage { DevicePn = "Nq3" });
        device.Metadata.UpdateFromProtobuf(new DaqifiOutMessage { DevicePn = "Nq3" });
        Assert.NotNull(device.Metadata.CapabilityDocument);

        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095,
        });

        Assert.Equal(2, OutputChannels(device).Count);
    }

    [Fact]
    public void Sync_AfterChannelsWerePopulated_LeavesTheInputChannelsAlone()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095,
            DigitalPortNum = 2,
        });
        var inputsBefore = device.GetChannelsSnapshot()
            .Where(c => c.Type != ChannelType.AnalogOutput)
            .ToList();

        device.SyncAnalogOutputChannelsFromCapabilities();

        var after = device.GetChannelsSnapshot();
        Assert.Equal(inputsBefore.Count + 2, after.Count);
        foreach (var input in inputsBefore)
        {
            Assert.Contains(input, after);
        }
    }

    [Fact]
    public void Sync_WhenTheOutputSetIsUnchanged_RaisesNoChannelsPopulated()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        var raised = 0;
        device.ChannelsPopulated += (_, _) => raised++;
        device.SyncAnalogOutputChannelsFromCapabilities();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Sync_WhenOutputsAppear_RaisesChannelsPopulatedWithTheInputCounts()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 3,
            AnalogInRes = 4095,
            DigitalPortNum = 2,
        });

        ChannelsPopulatedEventArgs? seen = null;
        device.ChannelsPopulated += (_, e) => seen = e;
        device.SyncAnalogOutputChannelsFromCapabilities();

        Assert.NotNull(seen);
        Assert.Equal(3, seen!.AnalogChannelCount);
        Assert.Equal(2, seen.DigitalChannelCount);
        Assert.Equal(7, seen.Channels.Count);
    }

    #endregion

    #region Writing a voltage

    [Fact]
    public void SetAnalogOutput_InRange_StagesThenLatchesAndRecordsTheValue()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        device.SetAnalogOutput(0, 2.5);

        Assert.Equal(
            new[] { "SOURce:VOLTage:LEVel 0,2.5", "CONFigure:DAC:UPDATE" },
            device.SentCommands);
        var output = OutputChannels(device)[0];
        Assert.Equal(2.5, output.OutputVoltage);
        Assert.Null(output.PendingVoltage);
    }

    [Theory]
    [InlineData(10.5)]
    [InlineData(-0.5)]
    public void SetAnalogOutput_OutOfTheStatedRange_ThrowsAndSendsNothing(double voltage)
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(0, voltage));
        Assert.Contains("0 to 10", ex.Message);
        Assert.Empty(device.SentCommands);
        Assert.Null(OutputChannels(device)[0].OutputVoltage);
    }

    [Fact]
    public void SetAnalogOutput_UsesEachChannelsOwnRange()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        // -2 V is out of channel 0's 0..10 V range but inside channel 1's -5..5 V.
        Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(0, -2.0));
        device.SetAnalogOutput(1, -2.0);

        Assert.Equal(-2.0, OutputChannels(device)[1].OutputVoltage);
    }

    [Fact]
    public void SetAnalogOutput_DeviceDescribedNoDacChannels_StillWritesByNumber()
    {
        // The only analog-output path this library has ever had addresses the DAC by number
        // with no channel model behind it. Firmware that does not describe its DACs must keep
        // working rather than start being refused.
        var device = new CapturingAnalogOutputDevice();
        device.Connect();

        device.SetAnalogOutput(3, 42.0);

        Assert.Equal(
            new[] { "SOURce:VOLTage:LEVel 3,42", "CONFigure:DAC:UPDATE" },
            device.SentCommands);
    }

    [Fact]
    public void SetAnalogOutput_NegativeChannel_ThrowsAndSendsNothing()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        Assert.Throws<ArgumentOutOfRangeException>(() => device.SetAnalogOutput(-1, 1.0));
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public void SetAnalogOutput_NotConnected_ThrowsAndSendsNothing()
    {
        var device = new CapturingAnalogOutputDevice();

        Assert.Throws<DeviceNotConnectedException>(() => device.SetAnalogOutput(0, 1.0));
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public void StageAnalogOutput_DoesNotDriveThePinUntilTheLatch()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        device.StageAnalogOutput(0, 1.5);

        Assert.Equal(new[] { "SOURce:VOLTage:LEVel 0,1.5" }, device.SentCommands);
        var output = OutputChannels(device)[0];
        Assert.Equal(1.5, output.PendingVoltage);
        Assert.Null(output.OutputVoltage);
    }

    [Fact]
    public void LatchAnalogOutputs_AppliesEveryStagedChannelTogether()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        device.StageAnalogOutput(0, 1.5);
        device.StageAnalogOutput(1, -1.5);
        device.LatchAnalogOutputs();

        Assert.Equal(
            new[] { "SOURce:VOLTage:LEVel 0,1.5", "SOURce:VOLTage:LEVel 1,-1.5", "CONFigure:DAC:UPDATE" },
            device.SentCommands);
        var outputs = OutputChannels(device);
        Assert.Equal(1.5, outputs[0].OutputVoltage);
        Assert.Equal(-1.5, outputs[1].OutputVoltage);
        Assert.All(outputs, c => Assert.Null(c.PendingVoltage));
    }

    [Fact]
    public void LatchAnalogOutputs_WithNothingStaged_StillCommandsTheDevice()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        device.LatchAnalogOutputs();

        Assert.Equal(new[] { "CONFigure:DAC:UPDATE" }, device.SentCommands);
        Assert.All(OutputChannels(device), c => Assert.Null(c.OutputVoltage));
    }

    [Fact]
    public void StageAnalogOutput_NotConnected_ThrowsAndStagesNothing()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.Disconnect();

        Assert.Throws<DeviceNotConnectedException>(() => device.StageAnalogOutput(0, 1.0));
        Assert.Null(OutputChannels(device)[0].PendingVoltage);
        Assert.Empty(device.SentCommands);
    }

    #endregion

    #region Enabling

    [Fact]
    public void EnableChannel_OnAnAnalogOutput_IsRefused()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        var output = OutputChannels(device)[0];

        var ex = Assert.Throws<ArgumentException>(() => device.EnableChannel(output));
        Assert.Contains("SetAnalogOutput", ex.Message);
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public void DisableAllChannels_IgnoresAnalogOutputs()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095,
        });
        device.SentCommands.Clear();

        device.DisableAllChannels();

        // Only the ADC mask is pushed; nothing addresses the DAC channels.
        Assert.Equal(new[] { "ENAble:VOLTage:DC 0" }, device.SentCommands);
    }

    #endregion

    #region Readback

    [Fact]
    public async Task GetAnalogOutputAsync_ParsesTheVoltageAndRecordsIt()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.CannedTextResponse.Add("2.5000");

        var volts = await device.GetAnalogOutputAsync(0);

        Assert.Equal(2.5, volts);
        Assert.Equal(new[] { "SOURce:VOLTage:LEVel? 0" }, device.SentCommands);
        Assert.Equal(2.5, OutputChannels(device)[0].OutputVoltage);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_SkipsEchoLinesBeforeTheNumber()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.CannedTextResponse.Add("SOURce:VOLTage:LEVel? 0");
        device.CannedTextResponse.Add(" -1.2500 ");

        Assert.Equal(-1.25, await device.GetAnalogOutputAsync(1));
    }

    [Fact]
    public async Task GetAnalogOutputAsync_DeviceReportsAnError_Throws()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.CannedTextResponse.Add("**ERROR: -113, \"Undefined header\"");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.GetAnalogOutputAsync(0));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_UnparseableAnswer_Throws()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();
        device.CannedTextResponse.Add("not a voltage");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.GetAnalogOutputAsync(0));
    }

    [Fact]
    public async Task GetAnalogOutputAsync_NoAnswerAtAll_Throws()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);
        device.SyncAnalogOutputChannelsFromCapabilities();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.GetAnalogOutputAsync(0));
        Assert.Contains("(nothing)", ex.Message);
    }

    [Fact]
    public async Task GetAnalogOutputAsync_NegativeChannel_Throws()
    {
        var device = ConnectedDeviceWith(TwoDacDocument);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.GetAnalogOutputAsync(-1));
    }

    [Fact]
    public async Task GetAnalogOutputAsync_NotConnected_Throws()
    {
        var device = new CapturingAnalogOutputDevice();

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.GetAnalogOutputAsync(0));
    }

    #endregion

    private static CapturingAnalogOutputDevice ConnectedDeviceWith(string capabilityJson)
    {
        Assert.True(CapabilityDocumentParser.TryParse(capabilityJson, out var document));

        var device = new CapturingAnalogOutputDevice();
        device.Connect();
        device.Metadata.ApplyCapabilityDocument(document!);
        device.SentCommands.Clear();
        return device;
    }

    private static List<IAnalogOutputChannel> OutputChannels(DaqifiDevice device)
        => device.GetChannelsSnapshot()
            .OfType<IAnalogOutputChannel>()
            .OrderBy(c => c.ChannelNumber)
            .ToList();

    /// <summary>
    /// A streaming device that records what it would have sent and answers text exchanges from
    /// a canned response, so the analog-output paths can be exercised without hardware
    /// (mirrors <c>TestableLanChipInfoDevice</c>).
    /// </summary>
    private sealed class CapturingAnalogOutputDevice : DaqifiStreamingDevice
    {
        public CapturingAnalogOutputDevice() : base("TestDevice") { }

        public List<string> SentCommands { get; } = new();

        public List<string> CannedTextResponse { get; } = new();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
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
            return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse.ToList());
        }
    }
}
