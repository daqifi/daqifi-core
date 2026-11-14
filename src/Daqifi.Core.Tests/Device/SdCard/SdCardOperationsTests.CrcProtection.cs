using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// The firmware gate on CRC-protected SD writes (issue #956), driven through
/// <see cref="DaqifiStreamingDevice"/> so the real <see cref="DaqifiDevice.EnsureSupported"/> and
/// requirement table decide, rather than a test double's answer.
/// </summary>
public partial class SdCardOperationsTests
{
    private static TestableSdCardStreamingDevice NyquistOn(string firmwareVersion)
    {
        var device = new TestableSdCardStreamingDevice("TestDevice");
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.FirmwareVersion = firmwareVersion;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        return device;
    }

    [Theory]
    [InlineData("3.8.0")]
    [InlineData("3.5.0")]
    public async Task SetSdCardCrcProtectionAsync_OnFirmwareBeforeV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing(string firmware)
    {
        var device = NyquistOn(firmware);
        device.Connect();

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), ex.RequiredVersion);
        Assert.Empty(device.SentMessages);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_OnFirmwareBeforeV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing()
    {
        var device = NyquistOn("3.8.0");
        device.Connect();

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.GetSdCardCrcProtectionAsync());

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Empty(device.SentMessages);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_OnV3_9_0_SendsTheCommandAndReportsItApplied()
    {
        var device = NyquistOn("3.9.0");
        // One canned reply serves every exchange: the storage space for the mount, the card's
        // identity with CRC on for the mode read, and an empty error queue's count for the set.
        device.CannedTextResponse = new()
        {
            "7797608448,7798738944",
            "3,\"SD\",\"SC64G\",8.0,305419896,2021-05,1,0",
            "0",
        };
        device.Connect();

        var result = await device.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.Contains(device.SentMessages, m => m.Data == "SYSTem:STORage:SD:PROTect 1");
        Assert.Contains(device.SentMessages, m => m.Data == "SYSTem:STORage:SD:SPACe?");
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_OnV3_9_0_ReadsTheDevice()
    {
        var device = NyquistOn("3.9.0");
        device.CannedTextResponse = new() { "1" };
        device.Connect();

        Assert.True(await device.GetSdCardCrcProtectionAsync());
        Assert.Equal("SYSTem:STORage:SD:PROTect?", device.SentMessages.Last().Data);
    }

    [Fact]
    public async Task CrcProtection_OnABoardWithNoSdSlot_ThrowsFeatureNotSupported()
    {
        var device = NyquistOn("3.9.0");
        device.Metadata.Capabilities = new DeviceCapabilities();
        device.Connect();

        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.SetSdCardCrcProtectionAsync(true));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.GetSdCardCrcProtectionAsync());
        Assert.Empty(device.SentMessages);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_CompetingQueueReaderBetweenCountAndPop_CannotGetIn()
    {
        // The set reads the error count in one exchange and pops its own -200 in the next. Held
        // only per exchange, the lock would let another caller drain that -200 and queue its own
        // error after the count read 1, and the pop would then discard that caller's error. The
        // device runs the whole change under its operation lock, so the competitor goes after.
        var errorQueue = new Queue<string>();
        var device = new ErrorQueueSdDevice(errorQueue);
        var popReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.BeforeExchange = async index =>
        {
            if (index == 2) // PROTect + ERRor:COUNt?, then the pop
            {
                popReached.SetResult();
                await releasePop.Task.ConfigureAwait(false);
            }
        };

        var set = Task.Run(() => device.SetSdCardCrcProtectionAsync(true));
        await popReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competitor = Task.Run(async () =>
        {
            var drained = await device.DrainErrorQueueAsync();
            await device.QueryAsync(ErrorQueueSdDevice.CompetingBadCommand);
            return drained;
        });
        await Task.Delay(100);
        Assert.False(competitor.IsCompleted, "The competing reader ran inside the CRC change.");

        releasePop.SetResult();
        await Task.WhenAll(set, competitor).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SdCardCrcProtectionResult.Pending, await set);
        Assert.Empty(await competitor);                                          // the set's -200 was already gone
        Assert.Equal(new[] { ErrorQueueSdDevice.CompetingCallersError }, errorQueue); // and the competitor's error survives
    }

    /// <summary>
    /// A 3.9.0 Nyquist with a FIFO SCPI error queue, answering the way the bench Nq1 does for a
    /// change it stores but cannot apply yet: <c>PROTect</c> queues a -200 and volunteers it
    /// inline, <c>SYSTem:ERRor:COUNt?</c> reports the depth without popping, and
    /// <c>SYSTem:ERRor?</c> pops the oldest entry. Each exchange takes the operation lock, as the
    /// real text exchange does (mirrors <c>WifiLinkStatusTests</c>' double).
    /// </summary>
    private sealed class ErrorQueueSdDevice : DaqifiStreamingDevice
    {
        public const string CompetingBadCommand = "SYSTem:BOGUSquery?";
        public const string CompetingCallersError = "-113,\"Undefined header\"";

        private readonly Queue<string> _errorQueue;
        private readonly List<string> _sent = new();
        private int _exchangeCount;

        public ErrorQueueSdDevice(Queue<string> errorQueue) : base("TestDevice")
        {
            _errorQueue = errorQueue;
            Connect();
            Metadata.DeviceType = DeviceType.Nyquist1;
            Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
            Metadata.FirmwareVersion = "3.9.0";
        }

        /// <summary>
        /// Runs before each exchange asks for the operation lock, with the exchange's 1-based
        /// index: the gap between two exchanges a competing caller would have to use.
        /// </summary>
        public Func<int, Task>? BeforeExchange { get; set; }

        /// <summary>A raw query through the same exchange, standing in for another caller's command.</summary>
        public Task<IReadOnlyList<string>> QueryAsync(string scpi)
            => ExecuteTextCommandAsync(() => Send(new ScpiMessage(scpi)));

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                lock (_sent)
                {
                    _sent.Add(stringMessage.Data);
                }
            }
        }

        private IEnumerable<string> Answer(string command)
        {
            switch (command)
            {
                case "SYSTem:STORage:SD:PROTect 1":
                    _errorQueue.Enqueue("-200,\"Execution error\"");
                    return new[] { "**ERROR: -200, \"Execution error\"" };
                case "SYSTem:ERRor:COUNt?":
                    return new[] { _errorQueue.Count.ToString(CultureInfo.InvariantCulture) };
                case "SYSTem:ERRor?":
                    return new[] { _errorQueue.Count > 0 ? _errorQueue.Dequeue() : "0,\"No error\"" };
                case CompetingBadCommand:
                    _errorQueue.Enqueue(CompetingCallersError);
                    return new[] { "**ERROR: -113, \"Undefined header\"" };
                default:
                    throw new InvalidOperationException($"Unexpected command '{command}'.");
            }
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            if (BeforeExchange != null)
            {
                await BeforeExchange(Interlocked.Increment(ref _exchangeCount)).ConfigureAwait(false);
            }

            return await RunExclusiveAsync(async ct =>
            {
                try
                {
                    if (prepareAsync != null)
                    {
                        await prepareAsync(ct).ConfigureAwait(false);
                    }

                    ct.ThrowIfCancellationRequested();
                    int before;
                    lock (_sent)
                    {
                        before = _sent.Count;
                    }

                    setupAction();
                    List<string> commands;
                    lock (_sent)
                    {
                        commands = _sent.Skip(before).ToList();
                    }

                    return (IReadOnlyList<string>)commands.SelectMany(Answer).ToList();
                }
                finally
                {
                    if (finalizeAsync != null)
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
