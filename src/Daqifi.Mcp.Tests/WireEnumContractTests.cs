using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;
using Daqifi.Mcp.Tools;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The strings behind <see cref="WireEnums"/> (issue #1042). They are a contract with AI clients,
/// so they are spelled out here as literals rather than derived from either enum.
/// </summary>
public class WireEnumContractTests
{
    [Fact]
    public void EveryValue_HasItsOwnWireSpelling()
    {
        Assert.Equal(["unknown", "serial", "wifi", "hid"], Names<WireConnectionType>());
        Assert.Equal(
            ["disconnected", "connecting", "connected", "lost", "retrying", "failed"],
            Names<WireConnectionStatus>());
        Assert.Equal(["unknown", "nyquist1", "nyquist2", "nyquist3"], Names<WireDeviceType>());
        Assert.Equal(["analog", "digital", "analog_output"], Names<WireChannelType>());
        Assert.Equal(["unknown", "input", "output"], Names<WireChannelDirection>());
        Assert.Equal(["input", "output"], Names<WireDigitalDirection>());
        Assert.Equal(["protobuf", "json", "csv"], Names<WireSdLogFormat>());
        Assert.Equal(["ok", "disabled", "unavailable"], Names<WireVersionCheck>());
    }

    [Fact]
    public void EveryCoreValue_MapsToTheWireValueThatMeansIt()
    {
        // Walks Core's enums, so a member added there fails here instead of throwing at a caller.
        AssertMapped<ConnectionType, WireConnectionType>(v => v.ToWire());
        AssertMapped<DeviceType, WireDeviceType>(v => v.ToWire());
        AssertMapped<ChannelType, WireChannelType>(v => v.ToWire());
        AssertMapped<ChannelDirection, WireChannelDirection>(v => v.ToWire());
        AssertMapped<SdCardLogFormat, WireSdLogFormat>(v => v.ToWire());
        AssertMapped<WireDigitalDirection, ChannelDirection>(v => v.ToCore());
        AssertMapped<WireSdLogFormat, SdCardLogFormat>(v => v.ToCore());
    }

    [Theory]
    [InlineData(DeviceState.Disconnected, WireConnectionStatus.Disconnected)]
    [InlineData(DeviceState.Connecting, WireConnectionStatus.Connecting)]
    [InlineData(DeviceState.Connected, WireConnectionStatus.Connected)]
    [InlineData(DeviceState.Initializing, WireConnectionStatus.Connected)]
    [InlineData(DeviceState.Ready, WireConnectionStatus.Connected)]
    [InlineData(DeviceState.InitializationFailed, WireConnectionStatus.Connected)]
    [InlineData(DeviceState.Disconnecting, WireConnectionStatus.Connected)]
    [InlineData(DeviceState.Lost, WireConnectionStatus.Lost)]
    [InlineData(DeviceState.Retrying, WireConnectionStatus.Retrying)]
    [InlineData(DeviceState.Failed, WireConnectionStatus.Failed)]
    [InlineData(DeviceState.Disposed, WireConnectionStatus.Disconnected)]
    public void EveryDeviceState_MapsToWhetherThereIsAConnection(DeviceState state, WireConnectionStatus wire)
    {
        Assert.Equal(wire, state.ToWire());
    }

    [Fact]
    public void EveryDeviceState_IsMapped()
    {
        // ToWire throws on a value it does not know.
        Assert.All(Enum.GetValues<DeviceState>(), state => state.ToWire());
    }

    [Fact]
    public void AResult_CarriesTheWireSpelling()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 1, digitalChannels: 1, analogOutputs: 1);

        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            agent.ListChannels(AgentHarness.DeviceId), ToolRegistration.SerializerOptions);

        var channels = json.GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(
            ["analog", "analog_output", "digital"],
            channels.Select(c => c.GetProperty("type").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(channels, c => Assert.Contains(c.GetProperty("direction").GetString(), new[] { "input", "output", "unknown" }));
    }

    private static void AssertMapped<TFrom, TTo>(Func<TFrom, TTo> map)
        where TFrom : struct, Enum
        where TTo : struct, Enum
    {
        // The member names agree today, which is what makes a crossed arm detectable. If Core
        // renames one, change the expectation here; the wire spelling above stays.
        foreach (var value in Enum.GetValues<TFrom>())
        {
            Assert.Equal(value.ToString(), map(value).ToString());
        }
    }

    private static string[] Names<T>()
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(ToolRegistration.WireName)];
}
