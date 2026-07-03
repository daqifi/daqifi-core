using Daqifi.Mcp;

namespace Daqifi.Mcp.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void Parse_NoArgs_DefaultsToControlWithNoClamp()
    {
        var options = ServerOptions.Parse(Array.Empty<string>());
        Assert.False(options.ReadOnly);
        Assert.Null(options.MaxSampleRateHz);
    }

    [Fact]
    public void Parse_ReadOnlyFlag_SetsReadOnly()
    {
        Assert.True(ServerOptions.Parse(new[] { "--read-only" }).ReadOnly);
    }

    [Fact]
    public void Parse_MaxSampleRate_SetsClamp()
    {
        Assert.Equal(500, ServerOptions.Parse(new[] { "--max-sample-rate-hz", "500" }).MaxSampleRateHz);
    }

    [Fact]
    public void Parse_MaxSampleRate_NonNumeric_IsIgnored()
    {
        Assert.Null(ServerOptions.Parse(new[] { "--max-sample-rate-hz", "fast" }).MaxSampleRateHz);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Parse_MaxSampleRate_NonPositive_IsIgnored(string value)
    {
        Assert.Null(ServerOptions.Parse(new[] { "--max-sample-rate-hz", value }).MaxSampleRateHz);
    }
}

public class DaqifiAgentTests
{
    private static DaqifiAgent NewAgent(bool readOnly = false) =>
        new(new ServerOptions { ReadOnly = readOnly });

    [Fact]
    public void ListConnected_StartsEmpty()
    {
        Assert.Empty(NewAgent().ListConnected());
    }

    [Fact]
    public void GetStatus_UnknownDevice_ThrowsWithActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewAgent().GetStatus("serial:NOPE"));
        Assert.Contains("not connected", ex.Message);
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_UnknownDeviceId_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().ConnectAsync("never-discovered", CancellationToken.None));
        Assert.Contains("discover_devices", ex.Message);
    }

    [Fact]
    public async Task Disconnect_UnknownDevice_ReturnsMessageRatherThanThrows()
    {
        Assert.Contains("was not connected", await NewAgent().DisconnectAsync("serial:NOPE"));
    }

    [Fact]
    public async Task SetSampleRate_BelowOne_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewAgent().SetSampleRateAsync("x", 0));
        Assert.Contains(">= 1", ex.Message);
    }

    [Fact]
    public async Task SetSampleRate_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).SetSampleRateAsync("x", 100));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task ConfigureAnalogChannels_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).ConfigureAnalogChannelsAsync("x", new[] { 0, 1 }));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task ConfigureDigitalChannels_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).ConfigureDigitalChannelsAsync("x", new[] { 0, 1 }));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task SetDigitalDirection_InvalidDirection_ThrowsBeforeDeviceLookup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().SetDigitalDirectionAsync("x", 0, "sideways"));
        Assert.Contains("'input' or 'output'", ex.Message);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("OUTPUT")]
    [InlineData(" out ")]
    public async Task SetDigitalDirection_ValidDirection_FailsOnUnknownDeviceNotParsing(string direction)
    {
        // Direction strings parse (case/whitespace-insensitive), so the failure is the missing device.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().SetDigitalDirectionAsync("serial:NOPE", 0, direction));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task SetDigitalOutput_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).SetDigitalOutputAsync("x", 0, high: true));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task SetDigitalOutput_UnknownDevice_ThrowsWithActionableMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().SetDigitalOutputAsync("serial:NOPE", 3, high: true));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task SetPwmOutput_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).SetPwmOutputAsync("x", 4, dutyCyclePercent: 50, frequencyHz: 1000));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task SetPwmOutput_UnknownDevice_ThrowsWithActionableMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().SetPwmOutputAsync("serial:NOPE", 4, dutyCyclePercent: 50, frequencyHz: 1000));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task DisablePwm_InReadOnlyMode_IsBlocked()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).DisablePwmAsync("x", 4));
        Assert.Contains("read-only", ex.Message);
    }
}
