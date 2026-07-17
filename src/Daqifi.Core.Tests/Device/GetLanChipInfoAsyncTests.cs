using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Device;

public class GetLanChipInfoAsyncTests
{
    [Fact]
    public async Task GetLanChipInfoAsync_WhenDisconnected_Throws()
    {
        var device = new TestableLanChipInfoDevice("TestDevice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.GetLanChipInfoAsync());
    }

    [Fact]
    public async Task GetLanChipInfoAsync_WhenValidJson_ReturnsParsedInfo()
    {
        var device = new TestableLanChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "{\"ChipId\":1377184,\"FwVersion\":\"19.7.7\",\"BuildDate\":\"Mar 30 2022\"}" },
        };
        device.Connect();

        var info = await device.GetLanChipInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("19.7.7", info!.FwVersion);
        Assert.Equal(1377184, info.ChipId);
    }

    [Fact]
    public async Task GetLanChipInfoAsync_WhenScpiErrorNegative200_ThrowsLanNotInitialized()
    {
        // Closes #203: LAN:ENAbled=1 in saved settings but the WINC1500 state
        // machine hasn't reached INITIALIZED yet makes GETChipInfo? return
        // this specific SCPI error instead of JSON.
        var device = new TestableLanChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -200, \"Execution error\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<LanNotInitializedException>(() => device.GetLanChipInfoAsync());
    }

    [Fact]
    public async Task GetLanChipInfoAsync_WhenUnrelatedScpiError_ReturnsNull()
    {
        // Only -200 gets the specific LanNotInitialized treatment; any other
        // SCPI error still falls back to the generic "unavailable" null.
        var device = new TestableLanChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113, \"Undefined header\"" },
        };
        device.Connect();

        var info = await device.GetLanChipInfoAsync();

        Assert.Null(info);
    }

    /// <summary>
    /// A streaming device whose text-command exchange returns a canned response, so
    /// <see cref="DaqifiStreamingDevice.GetLanChipInfoAsync"/> can be tested without a real
    /// transport (mirrors <c>TestableDiagnosticsDevice</c>).
    /// </summary>
    private sealed class TestableLanChipInfoDevice : DaqifiStreamingDevice
    {
        public List<string> SentCommands { get; } = new();
        public List<string> CannedTextResponse { get; } = new();

        public TestableLanChipInfoDevice(string name, IPAddress? ipAddress = null)
            : base(name, ipAddress)
        {
        }

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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            setupAction();
            return Task.FromResult<IReadOnlyList<string>>(CannedTextResponse.ToList());
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return CannedTextResponse.ToList();
        }
    }
}
