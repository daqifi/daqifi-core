using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class DeviceDiagnosticsTests
{
    [Fact]
    public async Task GetSystemLogAsync_WhenDisconnected_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.GetSystemLogAsync());
    }

    [Fact]
    public async Task GetSystemLogAsync_SendsCommandAndParsesEntries()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "Test log message 1", "Test info message" },
        };
        device.Connect();

        var entries = await device.GetSystemLogAsync();

        Assert.Contains("SYSTem:LOG?", device.SentCommands);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Test log message 1", entries[0].Message);
    }

    [Fact]
    public async Task ClearSystemLogAsync_SendsCommand()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");
        device.Connect();

        await device.ClearSystemLogAsync();

        Assert.Contains("SYSTem:LOG:CLEar", device.SentCommands);
    }

    [Fact]
    public async Task ClearSystemLogAsync_WhenDisconnected_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.ClearSystemLogAsync());
    }

    [Fact]
    public async Task SetLogLevelAsync_SendsCommandAndParsesEcho()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "STREAM: 2 (ceiling 3)" },
        };
        device.Connect();

        var setting = await device.SetLogLevelAsync("STREAM", 2);

        Assert.Contains("SYSTem:LOG:LEVel STREAM,2", device.SentCommands);
        Assert.Equal("STREAM", setting.Module);
        Assert.Equal(2, setting.Level);
        Assert.Equal(3, setting.Ceiling);
    }

    [Fact]
    public async Task SetLogLevelAsync_WhenDeviceReturnsScpiError_ThrowsDiagnosticsException()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -224,\"Illegal parameter value\"" },
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(
            () => device.SetLogLevelAsync("STREAM", 2));
        Assert.NotEmpty(ex.RawDeviceResponse);
    }

    [Fact]
    public async Task SetLogLevelAsync_ValidatesArgumentsBeforeConnectionCheck()
    {
        // Disconnected device + bad module must surface ArgumentException (misuse),
        // not InvalidOperationException (state), matching other setters.
        var device = new TestableDiagnosticsDevice("TestDevice");

        await Assert.ThrowsAsync<ArgumentException>(() => device.SetLogLevelAsync("", 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.SetLogLevelAsync("STREAM", 9));
    }

    [Fact]
    public async Task GetCommandHistoryAsync_SendsCommandAndParsesCommands()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "Last 2 commands:", "2: *IDN?", "1: SYSTem:LOG:TEST" },
        };
        device.Connect();

        var commands = await device.GetCommandHistoryAsync();

        Assert.Contains("SYSTem:LOG:CMDHistory?", device.SentCommands);
        Assert.Equal(new[] { "*IDN?", "SYSTem:LOG:TEST" }, commands);
    }

    [Fact]
    public async Task GetCommandHistoryAsync_WhenNoHistory_ReturnsEmpty()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "No command history" },
        };
        device.Connect();

        Assert.Empty(await device.GetCommandHistoryAsync());
    }

    [Fact]
    public async Task TestSystemLogAsync_SendsCommand()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");
        device.Connect();

        await device.TestSystemLogAsync();

        Assert.Contains("SYSTem:LOG:TEST", device.SentCommands);
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_SendsCommandAndParsesCount()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "3" },
        };
        device.Connect();

        var count = await device.GetSystemErrorCountAsync();

        Assert.Contains("SYSTem:ERRor:COUNt?", device.SentCommands);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_WhenUnparseable_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "not a number" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.GetSystemErrorCountAsync());
    }

    [Fact]
    public async Task GetStreamStatsAsync_SendsCommandAndParsesStats()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "TotalSamplesStreamed=15000", "QueueDroppedSamples=0" },
        };
        device.Connect();

        var stats = await device.GetStreamStatsAsync();

        Assert.Contains("SYSTem:STReam:STATS?", device.SentCommands);
        Assert.Equal(15000UL, stats.TotalSamplesStreamed);
    }

    [Fact]
    public async Task GetStreamStatsAsync_WhenUnparseable_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -200,\"Execution error\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.GetStreamStatsAsync());
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_SendsCommandAndParsesValues()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "HeapTotal=75000", "HeapFree=45000" },
        };
        device.Connect();

        var mem = await device.GetMemoryDiagnosticsAsync();

        Assert.Contains("SYSTem:MEMory:FREE?", device.SentCommands);
        Assert.Equal(75000UL, mem.HeapTotal);
        Assert.Equal(45000UL, mem.HeapFree);
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_WhenDisconnected_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.GetMemoryDiagnosticsAsync());
    }

    /// <summary>
    /// A streaming device whose text-command exchange returns a canned response and records the
    /// SCPI commands sent during the exchange, so diagnostics methods can be tested without a
    /// real transport (mirrors the SD card test harness).
    /// </summary>
    private sealed class TestableDiagnosticsDevice : DaqifiStreamingDevice
    {
        public List<string> SentCommands { get; } = new();
        public List<string> CannedTextResponse { get; } = new();

        public TestableDiagnosticsDevice(string name, IPAddress? ipAddress = null)
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
