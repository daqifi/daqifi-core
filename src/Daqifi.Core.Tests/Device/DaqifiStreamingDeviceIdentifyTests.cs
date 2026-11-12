using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// The identify LED flash (<c>SYSTem:IDENtify</c>, firmware v3.9.0) through the public device
/// surface, with the real requirement table deciding the gate. The command ordering and reply
/// classification are pinned in <c>DeviceAdministrationOperationsTests</c>; this file pins that a
/// device on older firmware is refused before anything reaches the wire, and that a 3.9.0 device
/// gets the right command.
/// </summary>
public class DaqifiStreamingDeviceIdentifyTests
{
    [Theory]
    [InlineData(true, "SYSTem:IDENtify 1")]
    [InlineData(false, "SYSTem:IDENtify 0")]
    public async Task SetIdentifyAsync_OnV3_9_0_SendsTheCommand(bool enabled, string expected)
    {
        var device = ConnectedDeviceOn("3.9.0");

        await device.SetIdentifyAsync(enabled);

        Assert.Equal(new[] { expected }, device.SentCommands);
    }

    [Theory]
    [InlineData("3.8.0")]
    [InlineData("3.5.0")]
    [InlineData("3.9.0b1")] // a pre-release precedes the 3.9.0 release
    [InlineData("")]        // version not reported yet: fail closed
    public async Task SetIdentifyAsync_BelowV3_9_0_ThrowsAndSendsNothing(string firmwareVersion)
    {
        var device = ConnectedDeviceOn(firmwareVersion);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.SetIdentifyAsync(true));

        Assert.Equal(DeviceFeature.Identify, ex.Feature);
        Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), ex.RequiredVersion);
        Assert.Empty(device.SentCommands);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task GetIdentifyAsync_OnV3_9_0_QueriesAndParses(string reply, bool expected)
    {
        var device = ConnectedDeviceOn("3.9.0");
        device.CannedTextResponse.Add(reply);

        Assert.Equal(expected, await device.GetIdentifyAsync());
        Assert.Equal(new[] { "SYSTem:IDENtify?" }, device.SentCommands);
    }

    [Fact]
    public async Task GetIdentifyAsync_OnV3_8_0_ThrowsWithoutAnExchange()
    {
        var device = ConnectedDeviceOn("3.8.0");
        device.CannedTextResponse.Add("1");

        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetIdentifyAsync());

        Assert.Equal(0, device.TextExchanges);
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task GetIdentifyAsync_OnV3_9_0_AnsweredWithUndefinedHeader_ReportsV3_9_0AsTheRequiredVersion()
    {
        // The device claims 3.9.0 but does not know the command: its answer wins (ADR 0001).
        var device = ConnectedDeviceOn("3.9.0");
        device.CannedTextResponse.Add("**ERROR: -113, \"Undefined header\"");

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetIdentifyAsync());

        Assert.Equal(DeviceFeature.Identify, ex.Feature);
        Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), ex.RequiredVersion);
    }

    [Fact]
    public async Task SetIdentifyAsync_WhenNotConnected_Throws()
    {
        var device = new CapturingIdentifyDevice();
        device.Metadata.FirmwareVersion = "3.9.0";

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.SetIdentifyAsync(true));
        Assert.Empty(device.SentCommands);
    }

    private static CapturingIdentifyDevice ConnectedDeviceOn(string firmwareVersion)
    {
        var device = new CapturingIdentifyDevice();
        device.Connect();
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.FirmwareVersion = firmwareVersion;
        device.SentCommands.Clear();
        return device;
    }

    /// <summary>
    /// A streaming device that records what it would have sent and answers text exchanges from a
    /// canned response (mirrors <c>CapturingAnalogOutputDevice</c>).
    /// </summary>
    private sealed class CapturingIdentifyDevice : DaqifiStreamingDevice
    {
        public CapturingIdentifyDevice() : base("TestDevice") { }

        public List<string> SentCommands { get; } = new();

        public List<string> CannedTextResponse { get; } = new();

        public int TextExchanges { get; private set; }

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
            TextExchanges++;
            setupAction();
            return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse.ToList());
        }
    }
}
