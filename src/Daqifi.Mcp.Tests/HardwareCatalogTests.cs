using System.Text.Json;
using Daqifi.Mcp.Tools;
using ModelContextProtocol;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the published board specifications (<see cref="HardwareCatalog"/>) and the
/// two ways they are served: the <c>get_hardware_info</c> tool and the MCP resources.
/// </summary>
/// <remarks>
/// A wrong number here is not a caught exception — it is an agent planning a rate the board
/// cannot reach, or telling someone the wrong board has drivable analog outputs. The figures are
/// pinned against the published specifications on daqifi.com/products, which is where they came
/// from. The nominal resolution and range are the weaker source by design: a connected device
/// declares its own, and ChannelResolutionContractTests covers that path.
/// </remarks>
public class HardwareCatalogTests
{
    [Fact]
    public void TheCatalog_CoversTheThreeNyquistBoards()
    {
        Assert.Equal(
            new[] { WireDeviceType.Nyquist1, WireDeviceType.Nyquist2, WireDeviceType.Nyquist3 },
            HardwareCatalog.All.Select(p => p.DeviceType));
    }

    [Fact]
    public void Nyquist1_IsTwelveBitUnipolarWithAPublishedRateCeiling()
    {
        var board = Assert.IsType<HardwareProfile>(HardwareCatalog.Find(WireDeviceType.Nyquist1));

        Assert.Equal(16, board.AnalogInputs.ChannelCount);
        Assert.Equal(12, board.AnalogInputs.NominalResolutionBits);
        Assert.Equal("0 to 5 V", board.AnalogInputs.NominalRange);
        Assert.False(board.AnalogInputs.SimultaneousSampling);
        // The ceiling with one channel enabled. The point of publishing it is that an agent can
        // plan a rate instead of discovering the limit from a set_sample_rate rejection.
        Assert.Equal(19250, board.AnalogInputs.MaxSampleRateHz);
        Assert.Equal(16, board.DigitalChannelCount);
    }

    [Fact]
    public void Nyquist2_IsTwentyFourBitAndItsDacsAreNotDrivenYet()
    {
        var board = Assert.IsType<HardwareProfile>(HardwareCatalog.Find(WireDeviceType.Nyquist2));

        Assert.Equal(16, board.AnalogInputs.ChannelCount);
        Assert.Equal(24, board.AnalogInputs.NominalResolutionBits);
        Assert.Equal("AD7173", board.AnalogInputs.AdcPart);

        // The board has the DAC hardware; this server does not drive it. Reporting the count as 0
        // would read as "no analog outputs on this board", which is a different and wrong fact.
        Assert.Equal(8, board.AnalogOutputs.ChannelCount);
        Assert.False(board.AnalogOutputs.DrivableFromThisServer);
        Assert.NotNull(board.AnalogOutputs.Note);
    }

    [Fact]
    public void Nyquist3_IsTheBipolarSimultaneousBoardWithDrivableOutputs()
    {
        var board = Assert.IsType<HardwareProfile>(HardwareCatalog.Find(WireDeviceType.Nyquist3));

        Assert.Equal(8, board.AnalogInputs.ChannelCount);
        Assert.Equal(18, board.AnalogInputs.NominalResolutionBits);
        Assert.Equal("-10 to +10 V", board.AnalogInputs.NominalRange);
        Assert.Equal("AD7609", board.AnalogInputs.AdcPart);
        // The only board where the values in one captured row share an instant.
        Assert.True(board.AnalogInputs.SimultaneousSampling);
        Assert.Equal(8, board.AnalogOutputs.ChannelCount);
        Assert.True(board.AnalogOutputs.DrivableFromThisServer);
    }

    [Fact]
    public void OnlyNyquist3_ClaimsDrivableAnalogOutputs()
    {
        // set_analog_output refuses every other board outright, and this flag is what tells an
        // agent that before it tries. The two statements have to agree.
        var drivable = HardwareCatalog.All
            .Where(p => p.AnalogOutputs.DrivableFromThisServer)
            .Select(p => p.DeviceType);

        Assert.Equal(new[] { WireDeviceType.Nyquist3 }, drivable);
    }

    [Fact]
    public void AnUnknownRateCeiling_IsNullRatherThanAGuess()
    {
        // Only Nyquist 1 publishes a single-channel ceiling. A plausible-looking number for the
        // others would be an agent planning a rate against a figure nobody measured.
        var withCeiling = HardwareCatalog.All
            .Where(p => p.AnalogInputs.MaxSampleRateHz is not null)
            .Select(p => p.DeviceType);

        Assert.Equal(new[] { WireDeviceType.Nyquist1 }, withCeiling);
    }

    [Fact]
    public void Find_HasNothingForABoardDiscoveryCouldNotIdentify()
    {
        // "unknown" is what discovery reports for a device whose type it could not establish, so
        // it must not resolve to some default board.
        Assert.Null(HardwareCatalog.Find(WireDeviceType.Unknown));
    }

    [Fact]
    public void GetHardwareInfo_WithNoArgument_ReportsEveryBoard()
    {
        var reported = DaqifiTools.GetHardwareInfo().Boards;

        Assert.Equal(HardwareCatalog.All.Count, reported.Count);
        Assert.Equal(HardwareCatalog.All.Select(p => p.DeviceType), reported.Select(p => p.DeviceType));
    }

    [Fact]
    public void GetHardwareInfo_WithABoard_ReportsJustThatOne()
    {
        var reported = Assert.Single(DaqifiTools.GetHardwareInfo(WireDeviceType.Nyquist1).Boards);

        Assert.Equal(WireDeviceType.Nyquist1, reported.DeviceType);
    }

    [Fact]
    public void GetHardwareInfo_ForAnUnknownBoard_SaysWhichBoardsItKnows()
    {
        // An empty list would read as "this board has nothing to report" rather than "ask about a
        // different name", and the agent would have no way to find the name that works.
        var ex = Assert.Throws<McpException>(() => DaqifiTools.GetHardwareInfo(WireDeviceType.Unknown));

        Assert.Contains("nyquist1", ex.Message);
        Assert.Contains("nyquist3", ex.Message);
        Assert.Contains("list_channels", ex.Message);
    }

    [Fact]
    public void EveryBoardInTheCatalog_HasAResource()
    {
        // Resources are the only route for a user attaching the data by hand, so a board added to
        // the catalog without one is served to the tool and invisible in the client.
        var uris = AdvertisedMcpResources.All
            .Select(r => r.ProtocolResource?.Uri)
            .ToList();

        Assert.All(
            HardwareCatalog.All,
            board => Assert.Contains(
                $"daqifi://hardware/{ToolRegistration.WireName(board.DeviceType)}",
                uris));
    }

    [Fact]
    public void TheResources_AreTheCatalogAsJson_WithNoExtraOnesAdvertised()
    {
        Assert.Equal(HardwareCatalog.All.Count, AdvertisedMcpResources.All.Count);

        foreach (var resource in AdvertisedMcpResources.All)
        {
            Assert.Equal("application/json", resource.ProtocolResource?.MimeType);
        }
    }

    [Fact]
    public void TheResourceJson_DeserializesBackIntoTheProfileItCameFrom()
    {
        // The field names a client sees in a resource read have to be the ones it sees in
        // get_hardware_info's structuredContent, or the same fact is spelled two ways. Both go
        // through ToolRegistration.SerializerOptions, and this is what pins that.
        var board = HardwareCatalog.All.Single(p => p.DeviceType == WireDeviceType.Nyquist3);

        var json = HardwareCatalog.ToJson(board);
        var round = JsonSerializer.Deserialize<HardwareProfile>(json, ToolRegistration.SerializerOptions);

        Assert.Equal(board, round);
    }

    [Fact]
    public void TheResourceJson_WritesAFieldItDoesNotKnowAsNull()
    {
        // Rather than omitting it: an absent key is indistinguishable from a key the reader
        // forgot to look for, while an explicit null says "nobody published this".
        var json = HardwareCatalog.ToJson(HardwareCatalog.All.Single(p => p.DeviceType == WireDeviceType.Nyquist2));

        using var parsed = JsonDocument.Parse(json);
        var inputs = parsed.RootElement.GetProperty("analogInputs");
        Assert.Equal(JsonValueKind.Null, inputs.GetProperty("maxSampleRateHz").ValueKind);
    }
}
