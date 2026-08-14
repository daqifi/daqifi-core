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

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.GetSystemLogAsync());
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
    public async Task GetSystemLogAsync_WhenBufferEmpty_ReturnsEmpty()
    {
        // No lines = genuinely empty buffer (firmware writes nothing); must not throw.
        var device = new TestableDiagnosticsDevice("TestDevice");
        device.Connect();

        Assert.Empty(await device.GetSystemLogAsync());
    }

    [Fact]
    public async Task GetSystemLogAsync_WhenErrorOnlyResponse_Throws()
    {
        // An error-only response must not masquerade as an empty log.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113,\"Undefined header\"" },
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.GetSystemLogAsync());
        Assert.NotEmpty(ex.RawDeviceResponse);
    }

    [Fact]
    public async Task ClearSystemLogAsync_SendsCommand()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "Log cleared" },
        };
        device.Connect();

        await device.ClearSystemLogAsync();

        Assert.Contains("SYSTem:LOG:CLEar", device.SentCommands);
    }

    [Fact]
    public async Task ClearSystemLogAsync_WhenErrorOnlyResponse_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113,\"Undefined header\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.ClearSystemLogAsync());
    }

    [Fact]
    public async Task ClearSystemLogAsync_WhenDisconnected_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice");

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.ClearSystemLogAsync());
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
    public async Task GetCommandHistoryAsync_WhenErrorOnlyResponse_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113,\"Undefined header\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.GetCommandHistoryAsync());
    }

    [Fact]
    public async Task TestSystemLogAsync_SendsCommand()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "Added test log messages" },
        };
        device.Connect();

        await device.TestSystemLogAsync();

        Assert.Contains("SYSTem:LOG:TEST", device.SentCommands);
    }

    [Fact]
    public async Task TestSystemLogAsync_WhenErrorOnlyResponse_Throws()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -113,\"Undefined header\"" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.TestSystemLogAsync());
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

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.GetMemoryDiagnosticsAsync());
    }

    // ---------------------------------------------------------------------------------------
    // Issue #537: a diagnostics query issued while the device is streaming gets protobuf frame
    // bytes welded onto the front of its first reply line, because pausing Core's protobuf reader
    // does not stop the firmware. CorruptedFirstLine below is the shape captured off the bench --
    // the key is destroyed, the value survives -- and the tolerant parser used to drop the pair
    // and report the rest as a complete reading.
    // ---------------------------------------------------------------------------------------

    /// <summary>A first reply line as it arrives mid-stream: binary prefix, real key, real value.</summary>
    private const string CorruptedFirstLine = "\u0008\uFFFD\\3\uFFFD\u0004\u0012\u0003\u0008\u0000TotalSamplesStreamed=203";

    [Fact]
    public async Task GetStreamStatsAsync_WhenStreamFramesCorruptTheReply_ThrowsInsteadOfLosingTheCounter()
    {
        // Before #537 this returned successfully with a healthy-looking Values dictionary and
        // TotalSamplesStreamed == null: the headline counter, silently gone.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { CorruptedFirstLine, "QueueDroppedSamples=0", "TimerISRCalls=933" },
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => device.GetStreamStatsAsync());

        // The raw reply is retained so a caller can still log or salvage what arrived.
        Assert.Equal(3, ex.RawDeviceResponse.Count);
        Assert.Contains("TotalSamplesStreamed", ex.RawDeviceResponse[0]);
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_WhenStreamFramesCorruptTheReply_ThrowsCorruptedResponse()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u00000" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => device.GetSystemErrorCountAsync());
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_WhenStreamFramesCorruptTheReply_ThrowsCorruptedResponse()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u0000HeapTotal=75000", "HeapFree=45000" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => device.GetMemoryDiagnosticsAsync());
    }

    [Fact]
    public async Task SetLogLevelAsync_WhenStreamFramesCorruptTheEcho_ThrowsCorruptedResponse()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u0000STREAM: 2 (ceiling 3)" },
        };
        device.Connect();

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => device.SetLogLevelAsync("STREAM", 2));
    }

    [Fact]
    public async Task SetLogLevelAsync_WhenTheDeviceRejectedTheRequest_ReportsTheRejectionNotTheCorruption()
    {
        // A device that answered "no" gave a real answer; that diagnosis outranks "your reply was
        // mangled", so the rejection check deliberately runs first.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "**ERROR: -224,\"Illegal parameter value\"", "\u0000noise" },
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => device.SetLogLevelAsync("STREAM", 2));
        Assert.IsNotType<DeviceDiagnosticsCorruptedResponseException>(ex);
    }

    [Fact]
    public async Task GetStreamStatsAsync_WhenReplyContainsATab_IsNotTreatedAsCorrupted()
    {
        // Tab is real device output (it appears as a SCPI token delimiter), so it must not be
        // mistaken for interleaved binary and fail an otherwise clean read.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "TotalSamplesStreamed\t=\t15000" },
        };
        device.Connect();

        var stats = await device.GetStreamStatsAsync();

        Assert.Equal(15000UL, stats.TotalSamplesStreamed);
    }

    [Fact]
    public async Task GetSystemLogAsync_WhenALineIsCorrupted_StillReturnsTheSurvivingEntries()
    {
        // Deliberately NOT guarded: the log read clears the buffer on the device, so the entries
        // that did arrive are all anyone will ever get. Throwing them away would destroy more than
        // it protects, and a missing entry is visible in the result anyway.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u0000Test log message 1", "Test info message" },
        };
        device.Connect();

        var entries = await device.GetSystemLogAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Test info message", entries[1].Message);
    }

    [Fact]
    public async Task GetCommandHistoryAsync_WhenALineIsCorrupted_StillReturnsTheSurvivingCommands()
    {
        // Same reasoning as the system log: one lost line out of several is visible in the result.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u00002: SYSTem:LOG?", "1: SYSTem:LOG:CMDHistory?" },
        };
        device.Connect();

        var commands = await device.GetCommandHistoryAsync();

        Assert.Contains("SYSTem:LOG:CMDHistory?", commands);
    }

    [Fact]
    public async Task ClearSystemLogAsync_WhenTheAckIsCorrupted_DoesNotReportAFailure()
    {
        // Deliberately NOT guarded: the reply is an ack, not a result. The command ran; failing it
        // over a mangled ack would report a failure that did not happen.
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u0000Log cleared" },
        };
        device.Connect();

        await device.ClearSystemLogAsync();

        Assert.Contains("SYSTem:LOG:CLEar", device.SentCommands);
    }

    [Fact]
    public async Task TestSystemLogAsync_WhenTheAckIsCorrupted_DoesNotReportAFailure()
    {
        var device = new TestableDiagnosticsDevice("TestDevice")
        {
            CannedTextResponse = { "\u0000Added test log messages" },
        };
        device.Connect();

        await device.TestSystemLogAsync();

        Assert.Contains("SYSTem:LOG:TEST", device.SentCommands);
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

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null)
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
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return CannedTextResponse.ToList();
        }
    }
}
