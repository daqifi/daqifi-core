using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Covers the multi-exchange operations on <see cref="DaqifiStreamingDevice"/> that must run
/// under <see cref="DaqifiDevice.RunExclusiveAsync(Func{CancellationToken, Task}, CancellationToken)"/>
/// so another caller cannot land a command in the middle of them: the confirming calibration
/// commands (drain, then command + <c>SYSTem:ERRor?</c>) and the network reconfiguration (a dozen
/// sends, then the WiFi module's restart window).
/// </summary>
public class DaqifiStreamingDeviceExclusiveOperationsTests
{
    private static readonly string ErrorQueueQuery = ScpiMessageProducer.GetSystemError.Data;

    /// <summary>A command another caller sends that the device rejects, queuing <see cref="CompetingCallersError"/>.</summary>
    private const string CompetingBadCommand = "SYSTem:BOGUSquery?";
    private const string CompetingCallersError = "-113,\"Undefined header\"";

    /// <summary>The nine confirming calibration methods, each with arguments the device accepts.</summary>
    public static TheoryData<string> ConfirmingCommands => new()
    {
        nameof(DaqifiStreamingDevice.SaveAdcCalibrationAsync),
        nameof(DaqifiStreamingDevice.LoadAdcCalibrationAsync),
        nameof(DaqifiStreamingDevice.SetAdcCalibrationSlopeAsync),
        nameof(DaqifiStreamingDevice.SetAdcCalibrationOffsetAsync),
        nameof(DaqifiStreamingDevice.SaveFactoryAdcCalibrationAsync),
        nameof(DaqifiStreamingDevice.LoadFactoryAdcCalibrationAsync),
        nameof(DaqifiStreamingDevice.UseAdcCalibrationAsync),
        nameof(DaqifiStreamingDevice.SaveVoltagePrecisionAsync),
        nameof(DaqifiStreamingDevice.LoadVoltagePrecisionAsync),
    };

    private static Task Invoke(DaqifiStreamingDevice device, string method) => method switch
    {
        nameof(DaqifiStreamingDevice.SaveAdcCalibrationAsync) => device.SaveAdcCalibrationAsync(),
        nameof(DaqifiStreamingDevice.LoadAdcCalibrationAsync) => device.LoadAdcCalibrationAsync(),
        nameof(DaqifiStreamingDevice.SetAdcCalibrationSlopeAsync) => device.SetAdcCalibrationSlopeAsync(0, 1.0),
        nameof(DaqifiStreamingDevice.SetAdcCalibrationOffsetAsync) => device.SetAdcCalibrationOffsetAsync(0, 0.0),
        nameof(DaqifiStreamingDevice.SaveFactoryAdcCalibrationAsync) => device.SaveFactoryAdcCalibrationAsync(),
        nameof(DaqifiStreamingDevice.LoadFactoryAdcCalibrationAsync) => device.LoadFactoryAdcCalibrationAsync(),
        nameof(DaqifiStreamingDevice.UseAdcCalibrationAsync) => device.UseAdcCalibrationAsync(1),
        nameof(DaqifiStreamingDevice.SaveVoltagePrecisionAsync) => device.SaveVoltagePrecisionAsync(),
        nameof(DaqifiStreamingDevice.LoadVoltagePrecisionAsync) => device.LoadVoltagePrecisionAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    [Theory]
    [MemberData(nameof(ConfirmingCommands))]
    public async Task ConfirmingCalibrationCommand_AnotherCallersRejectedCommand_CannotLandBetweenDrainAndVerdict(string method)
    {
        // The drain empties the queue in one exchange and the command + SYSTem:ERRor? go out in
        // the next. A competing caller whose command the device rejects, landing in that gap,
        // would queue an error the verdict then pops — reporting an accepted command as refused.
        // The confirming method holds the operation lock across both, so the competitor runs only
        // after the verdict has been read.
        var errorQueue = new Queue<string>();
        var device = ConnectedDevice(errorQueue);
        var verdictReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerdict = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.BeforeExchange = async index =>
        {
            if (index == 2) // the drain's SYSTem:ERRor?, then the command + its SYSTem:ERRor?
            {
                verdictReached.SetResult();
                await releaseVerdict.Task.ConfigureAwait(false);
            }
        };

        var confirm = Task.Run(() => Invoke(device, method));
        await verdictReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competitor = Task.Run(() => device.QueryAsync(CompetingBadCommand));
        await Task.Delay(100);
        Assert.False(competitor.IsCompleted, "The competing command ran between the drain and the verdict.");

        releaseVerdict.SetResult();
        await Task.WhenAll(confirm, competitor).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { CompetingCallersError }, errorQueue); // the competitor's error was not taken as this command's
    }

    [Theory]
    [InlineData(nameof(DaqifiStreamingDevice.SetAdcCalibrationSlopeAsync), -1, true)]
    [InlineData(nameof(DaqifiStreamingDevice.SetAdcCalibrationSlopeAsync), -1, false)]
    [InlineData(nameof(DaqifiStreamingDevice.SetAdcCalibrationOffsetAsync), -1, true)]
    [InlineData(nameof(DaqifiStreamingDevice.SetAdcCalibrationOffsetAsync), -1, false)]
    [InlineData(nameof(DaqifiStreamingDevice.UseAdcCalibrationAsync), 2, true)]
    [InlineData(nameof(DaqifiStreamingDevice.UseAdcCalibrationAsync), -1, false)]
    public async Task ConfirmingCalibrationCommand_InvalidArgument_ThrowsSynchronouslyWithoutWaitingForTheLock(
        string method,
        int invalidArgument,
        bool preCancelled)
    {
        // A bad channel or bank is the caller's mistake and is reported as such straight away:
        // not masked by a cancelled token, and not held back until another caller's long
        // operation (a download, a network update) releases the device.
        var device = ConnectedDevice(new Queue<string>());
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            lockHeld.SetResult();
            await releaseLock.Task.ConfigureAwait(false);
        }));
        await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        if (preCancelled)
        {
            cts.Cancel();
        }

        try
        {
            Func<Task> call = method switch
            {
                nameof(DaqifiStreamingDevice.SetAdcCalibrationSlopeAsync) => () => device.SetAdcCalibrationSlopeAsync(invalidArgument, 1.0, cts.Token),
                nameof(DaqifiStreamingDevice.SetAdcCalibrationOffsetAsync) => () => device.SetAdcCalibrationOffsetAsync(invalidArgument, 0.0, cts.Token),
                nameof(DaqifiStreamingDevice.UseAdcCalibrationAsync) => () => device.UseAdcCalibrationAsync(invalidArgument, cts.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
            };

            // Assert.Throws, not ThrowsAsync: the call itself must throw, before any task exists.
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = call(); });
        }
        finally
        {
            releaseLock.SetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_AnotherCallersQuery_WaitsForTheWholeReconfiguration()
    {
        // The reconfiguration sends its staged settings, the save and the apply, then holds for
        // the WiFi module's restart window. A competing caller's query must not run inside that
        // window (or between the sends): it waits until the update has finished.
        var device = ConnectedDevice(new Queue<string>());
        var applySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.OnSend = command =>
        {
            if (command == ScpiMessageProducer.ApplyNetworkLan.Data)
            {
                applySent.TrySetResult();
            }
        };

        device.Responder = command =>
        {
            Assert.Equal(CompetingBadCommand, command);
            return new[] { "**ERROR: -113, \"Undefined header\"" };
        };

        // Cancelling during the restart wait ends the wait early and completes normally (the
        // device has already committed), which keeps this test from sitting out the full window.
        using var endRestartWait = new CancellationTokenSource();
        var configuration = new NetworkConfiguration
        {
            Mode = WifiMode.ExistingNetwork,
            Ssid = "NewNetwork",
            SecurityType = WifiSecurityType.WpaPskPhrase,
            Password = "password123",
        };

        var update = Task.Run(() => device.UpdateNetworkConfigurationAsync(configuration, endRestartWait.Token));
        await applySent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competitor = Task.Run(() => device.QueryAsync(CompetingBadCommand));
        await Task.Delay(100);
        Assert.False(competitor.IsCompleted, "The competing query ran inside the module restart window.");

        endRestartWait.Cancel();
        await Task.WhenAll(update, competitor).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScpiMessageProducer.ApplyNetworkLan.Data, device.SentCommands[^2]); // nothing of the competitor's before the apply
        Assert.Equal(CompetingBadCommand, device.SentCommands[^1]);
    }

    /// <summary>
    /// A connected device with a FIFO SCPI error queue: <c>SYSTem:ERRor?</c> pops the oldest
    /// entry, the competitor's bad command queues an error, and every confirming command is
    /// accepted (it queues nothing).
    /// </summary>
    private static TestableExclusiveDevice ConnectedDevice(Queue<string> errorQueue)
    {
        var device = new TestableExclusiveDevice();
        device.Connect();
        device.Responder = command =>
        {
            if (command == ErrorQueueQuery)
            {
                return new[] { errorQueue.Count > 0 ? errorQueue.Dequeue() : "0,\"No error\"" };
            }

            if (command == CompetingBadCommand)
            {
                errorQueue.Enqueue(CompetingCallersError);
                return new[] { "**ERROR: -113, \"Undefined header\"" };
            }

            throw new InvalidOperationException($"Unexpected command '{command}'.");
        };
        return device;
    }

    /// <summary>
    /// A streaming device whose text exchange takes the real operation lock and answers from
    /// <see cref="Responder"/>, with a hook in the gap before each exchange — the gap a competing
    /// caller would have to use (mirrors <c>TestableWifiDevice</c> in <c>WifiLinkStatusTests</c>).
    /// </summary>
    private sealed class TestableExclusiveDevice : DaqifiStreamingDevice
    {
        public TestableExclusiveDevice() : base("TestDevice")
        {
        }

        private readonly object _sentGate = new();
        private readonly List<string> _sentCommands = new();

        public IReadOnlyList<string> SentCommands
        {
            get
            {
                lock (_sentGate)
                {
                    return _sentCommands.ToList();
                }
            }
        }

        /// <summary>Computes the reply from the last command the exchange sent.</summary>
        public Func<string, string[]> Responder { get; set; } = _ => Array.Empty<string>();

        /// <summary>Observes every command as it is sent.</summary>
        public Action<string>? OnSend { get; set; }

        /// <summary>Runs before each exchange asks for the operation lock, with the exchange's 1-based index.</summary>
        public Func<int, Task>? BeforeExchange { get; set; }

        private int _exchangeCount;

        /// <summary>A raw query through the same exchange, standing in for another caller's command.</summary>
        public Task<IReadOnlyList<string>> QueryAsync(string scpi)
            => ExecuteTextCommandAsync(() => Send(new ScpiMessage(scpi)));

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                lock (_sentGate)
                {
                    _sentCommands.Add(stringMessage.Data);
                }

                OnSend?.Invoke(stringMessage.Data);
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
            if (BeforeExchange != null)
            {
                await BeforeExchange(Interlocked.Increment(ref _exchangeCount)).ConfigureAwait(false);
            }

            // Each exchange takes the device's operation lock, as the real text exchange does, and
            // like it runs nested when this flow already holds the lock.
            return await RunExclusiveAsync(ct =>
            {
                ct.ThrowIfCancellationRequested();
                setupAction();
                return Task.FromResult<IReadOnlyList<string>>(Responder(SentCommands[^1]));
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
