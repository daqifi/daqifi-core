using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Device;

public class GetWifiChipInfoAsyncTests
{
    [Fact]
    public async Task GetWifiChipInfoAsync_WhenValidJson_ReturnsParsedInfo()
    {
        var device = new TestableWifiChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "{\"ChipId\":1377184,\"FwVersion\":\"19.7.7\",\"BuildDate\":\"Mar 30 2022\"}" },
        };
        device.Connect();

        var info = await device.GetWifiChipInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("19.7.7", info!.FirmwareVersion);
        Assert.Equal(1377184, info.ChipId);
    }

    [Fact]
    public async Task GetWifiChipInfoAsync_WhenScpiErrorNegative200_ThrowsWifiNotInitialized()
    {
        // Closes #203: LAN:ENAbled=1 in saved settings but the WINC1500 state
        // machine hasn't reached INITIALIZED yet makes GETChipInfo? return
        // this specific SCPI error instead of JSON.
        var device = new TestableWifiChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -200, \"Execution error\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<WifiNotInitializedException>(() => device.GetWifiChipInfoAsync());
    }

    [Fact]
    public async Task GetWifiChipInfoAsync_WhenUnrelatedScpiError_ReturnsNull()
    {
        // Only -200 gets the specific WifiNotInitialized treatment; any other
        // SCPI error still falls back to the generic "unavailable" null.
        var device = new TestableWifiChipInfoDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113, \"Undefined header\"" },
        };
        device.Connect();

        var info = await device.GetWifiChipInfoAsync();

        Assert.Null(info);
    }

    /// <summary>
    /// A streaming device whose text-command exchange returns a canned response, so
    /// <see cref="DaqifiStreamingDevice.GetWifiChipInfoAsync"/> can be tested without a real
    /// transport (mirrors <c>TestableDiagnosticsDevice</c>).
    /// </summary>
    private sealed class TestableWifiChipInfoDevice : DaqifiStreamingDevice
    {
        public List<string> SentCommands { get; } = new();
        public List<string> CannedTextResponse { get; } = new();

        public TestableWifiChipInfoDevice(string name)
            : base(name)
        {
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            TimeSpan? responseTimeout = null,
            TimeSpan? completionTimeout = null,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Honor the exchange's prepare phase the way the real device does: it runs first,
                // before anything this exchange sends (#396).
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                setupAction();
                return CannedTextResponse.ToList();
            }
            finally
            {
                // Honor the exchange's finalize phase the way the real device does: it runs
                // however the exchange ended, still inside the exchange (#407).
                if (finalizeAsync != null)
                {
                    await finalizeAsync().ConfigureAwait(false);
                }
            }
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            TimeSpan? responseTimeout = null,
            TimeSpan? completionTimeout = null,
            CancellationToken cancellationToken = default)
        {
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return CannedTextResponse.ToList();
        }
    }
}
