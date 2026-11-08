using Daqifi.Core.Channel;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the scaling facts <c>list_channels</c> reports about an analog channel:
/// ADC resolution, its bit depth, whether the device actually stated it, and the port range.
/// </summary>
/// <remarks>
/// These are the numbers Core divides a raw count by to produce volts, and the device declares
/// them in its capability document. Dropping them from the tool result is a silent failure: the
/// agent reports "2.47 V" either way, and nothing tells it whether that came off a 12-bit
/// converter or a 24-bit one, or whether the resolution behind it was a fallback guess.
/// </remarks>
public class ChannelResolutionContractTests
{
    [Fact]
    public void ListChannels_ReportsTheResolutionAndRangeTheDeviceDeclared()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 2, digitalChannels: 4);

        var analog = agent.ListChannels(AgentHarness.DeviceId)
            .Where(c => c.Type == nameof(ChannelType.Analog))
            .ToList();

        Assert.Equal(2, analog.Count);
        Assert.All(analog, c =>
        {
            // The double's capability document says analog_in_res=65535, so this is the device's
            // own answer travelling through rather than anything this server decided.
            Assert.Equal(65535u, c.Resolution);
            Assert.Equal(16, c.ResolutionBits);
            Assert.False(c.ResolutionIsAssumed);
            Assert.NotNull(c.PortRangeVolts);
        });
    }

    [Fact]
    public void ListChannels_SaysWhenTheResolutionWasAGuess()
    {
        // A device that omits analog_in_res gets an assumed 65535, and every scaled sample on it
        // can be systematically wrong. An agent quoting a voltage has to be able to see that, so
        // the flag travels with the channel instead of staying in a server-side log line.
        var (agent, _) = AgentHarness.WithConnectedDevice(
            analogChannels: 1, digitalChannels: 0, analogResolution: 0);

        var channel = Assert.Single(
            agent.ListChannels(AgentHarness.DeviceId),
            c => c.Type == nameof(ChannelType.Analog));

        Assert.True(channel.ResolutionIsAssumed);
        Assert.Equal(65535u, channel.Resolution);
    }

    [Fact]
    public void ListChannels_LeavesTheAnalogFieldsOffADigitalChannel()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 1, digitalChannels: 4);

        var digital = agent.ListChannels(AgentHarness.DeviceId)
            .Where(c => c.Type == nameof(ChannelType.Digital));

        // Null rather than 0: a digital channel has no ADC, and a zero would read as one with no
        // resolution at all.
        Assert.All(digital, c =>
        {
            Assert.Null(c.Resolution);
            Assert.Null(c.ResolutionBits);
            Assert.Null(c.ResolutionIsAssumed);
            Assert.Null(c.PortRangeVolts);
        });
    }

    [Theory]
    [InlineData(4095u, 12)]
    [InlineData(65535u, 16)]
    [InlineData(262143u, 18)]
    [InlineData(16777215u, 24)]
    public void ResolutionToBits_ConvertsAMaxCountToItsBitDepth(uint resolution, int bits)
    {
        // The device reports 2^n - 1, which is the divisor; bit depth is what a person and a
        // model think in.
        Assert.Equal(bits, ChannelInfo.ResolutionToBits(resolution));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1000u)]
    [InlineData(65534u)]
    public void ResolutionToBits_DeclinesToRoundACountThatIsNotTwoToTheN(uint resolution)
    {
        // Rounding 1000 to "10 bits" would be a number this server invented. Null says so, and
        // the raw resolution is still reported for anyone who wants to do their own arithmetic.
        Assert.Null(ChannelInfo.ResolutionToBits(resolution));
    }
}
