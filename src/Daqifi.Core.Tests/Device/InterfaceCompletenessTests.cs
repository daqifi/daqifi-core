using System.Reflection;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// What a caller holding only an interface can do, and which constructors are public (issue #1038).
/// </summary>
public class InterfaceCompletenessTests
{
    [Fact]
    public void IDevice_AnswersSupports()
    {
        var concrete = new RecordingDevice();
        concrete.UpdateMetadata(metadata => metadata with
        {
            DeviceType = DeviceType.Nyquist1,
            FirmwareVersion = "3.9.0"
        });
        IDevice device = concrete;

        Assert.True(device.Supports(DeviceFeature.Identify));
        Assert.False(device.Supports(DeviceFeature.AnalogOutput));
    }

    [Fact]
    public async Task IDevice_InitializeAsync_RequiresAConnection()
    {
        IDevice device = new RecordingDevice();

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.InitializeAsync());
    }

    [Fact]
    public void IStreamingDevice_StagesAndLatchesAnalogOutputs()
    {
        var concrete = new RecordingDevice();
        concrete.Connect();
        Assert.True(CapabilityDocumentParser.TryParse(
            """
            {"schema_version":2,"channels":[
              {"id":0,"kind":"analog-output","unit":"V","resolution_bits":12,"ranges":[{"min":0.000,"max":10.000}],"extensions":{}},
              {"id":1,"kind":"analog-output","unit":"V","resolution_bits":12,"ranges":[{"min":0.000,"max":10.000}],"extensions":{}}
            ]}
            """,
            out var document));
        concrete.UpdateMetadata(metadata => metadata.WithCapabilityDocument(document!));
        concrete.SyncAnalogOutputChannelsFromCapabilities();
        IStreamingDevice device = concrete;
        var outputs = device.GetChannelsSnapshot().OfType<IAnalogOutputChannel>().ToList();

        device.StageAnalogOutput(outputs[0], 1.5);
        device.StageAnalogOutput(outputs[1], 2.5);
        device.LatchAnalogOutputs();

        Assert.Equal(
            new[]
            {
                ScpiMessageProducer.SetAnalogOutputVoltage(0, 1.5).Data,
                ScpiMessageProducer.SetAnalogOutputVoltage(1, 2.5).Data,
                ScpiMessageProducer.UpdateDacOutputs.Data,
            },
            concrete.Sent);
        Assert.Equal(DaqifiStreamingDevice.MinimumPwmFrequencyHz, device.PwmFrequencyFloorHz);
    }

    [Fact]
    public async Task IStreamingDevice_Reads_RequireAConnection()
    {
        IStreamingDevice device = new RecordingDevice();

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.GetAnalogOutputAsync(new AnalogOutputChannel(0)));
        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.ReadCapabilityDocumentAsync());
        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.RefreshDeviceStatusAsync());
    }

    [Fact]
    public async Task IDeviceIdentification_IsReachableFromAnyDeviceTheFactoryReturns()
    {
        var concrete = new RecordingDevice();
        concrete.Connect();
        concrete.UpdateMetadata(metadata => metadata with { FirmwareVersion = "3.9.0" });
        IDevice device = concrete;

        var identification = Assert.IsAssignableFrom<IDeviceIdentification>(device);
        await identification.SetIdentifyAsync(true);

        Assert.Equal(new[] { ScpiMessageProducer.SetIdentify(true).Data }, concrete.Sent);
    }

    [Theory]
    [InlineData(typeof(DaqifiDevice))]
    [InlineData(typeof(DaqifiStreamingDevice))]
    public void EveryPublicConstructor_TakesATransport(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(constructors);
        Assert.All(constructors, c =>
            Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(IStreamTransport)));
    }

    [Fact]
    public void ADeviceWithNoTransport_ConnectsButCannotSend()
    {
        using var device = new TransportlessStreamingDevice("TestDevice");
        device.Connect();

        Assert.True(device.IsConnected);
        Assert.Throws<InvalidOperationException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
    }

    private sealed class RecordingDevice() : DaqifiStreamingDevice("TestDevice")
    {
        public List<string> Sent { get; } = [];

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> text)
            {
                Sent.Add(text.Data);
            }
        }
    }
}
