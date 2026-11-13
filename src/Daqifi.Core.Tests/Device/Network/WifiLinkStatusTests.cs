using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network;

/// <summary>
/// Covers <see cref="IWifiLinkStatusProvider"/> on <see cref="DaqifiStreamingDevice"/> (#955): the
/// firmware v3.9.0 live WiFi queries <c>LAN:CONnected?</c> and <c>LAN:NETType? 1</c>, their reply
/// parsing, the standby/disabled <c>-200</c> answer, and the v3.9.0 feature gate.
/// </summary>
/// <remarks>
/// Reply shapes are the firmware's at tag v3.9.0 (<c>SCPI_LANConnectedGet</c> /
/// <c>SCPI_LANNetModeGet</c> in <c>SCPILAN.c</c>): a bare mnemonic or a bare integer, and SCPI
/// errors volunteered inline as <c>**ERROR: code, "text"</c>.
/// </remarks>
public class WifiLinkStatusTests
{
    private static readonly string ConnectedQuery = ScpiMessageProducer.GetLanConnected.Data;
    private static readonly string ActiveModeQuery = ScpiMessageProducer.GetLanActiveWifiMode.Data;

    private static readonly string ErrorQueueQuery = ScpiMessageProducer.GetSystemError.Data;
    private static readonly string ErrorCountQuery = ScpiMessageProducer.GetSystemErrorCount.Data;

    /// <summary>A command another caller sends that the device rejects, queuing <see cref="CompetingCallersError"/>.</summary>
    private const string CompetingBadCommand = "SYSTem:BOGUSquery?";
    private const string CompetingCallersError = "-113,\"Undefined header\"";

    /// <summary>What <c>SYSTem:ERRor?</c> returned for the standby -200 on the bench.</summary>
    private const string StandbyQueueEntry = "-200,\"Execution error\"";

    /// <summary>
    /// What the bench Nq1 on firmware 3.9.0 volunteered for <c>CONnected?</c> in standby: the
    /// generic -200 text, not the custom message <c>SCPI_LANConnectedGet</c> passes.
    /// </summary>
    private const string WifiNotReadyOnTheWire = "**ERROR: -200, \"Execution error\"";

    #region Wire format

    [Fact]
    public void Queries_MatchTheFirmwareCommandTable()
    {
        Assert.Equal("SYSTem:COMMunicate:LAN:CONnected?", ConnectedQuery);
        Assert.Equal("SYSTem:COMMunicate:LAN:NETType? 1", ActiveModeQuery);
    }

    #endregion

    #region GetWifiLinkStateAsync — reply parsing

    [Theory]
    [InlineData("CONNECTED", WifiLinkState.Connected)]
    [InlineData("APIDLE", WifiLinkState.AccessPointIdle)]
    [InlineData("NOLINK", WifiLinkState.NoLink)]
    [InlineData("INITFAULT", WifiLinkState.InitializationFault)]
    [InlineData("INIT", WifiLinkState.Initializing)]
    public async Task GetWifiLinkStateAsync_EachFirmwareMnemonic_MapsToItsState(string mnemonic, WifiLinkState expected)
    {
        var device = ConnectedDevice(mnemonic);

        var state = await device.GetWifiLinkStateAsync();

        Assert.Equal(expected, state);
        Assert.Equal(new[] { ConnectedQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_ReplyWithSurroundingWhitespaceAndBlankLines_StillParses()
    {
        var device = ConnectedDevice("", "  NOLINK\r");

        Assert.Equal(WifiLinkState.NoLink, await device.GetWifiLinkStateAsync());
    }

    [Theory]
    [InlineData("INITF")]       // a prefix of INITFAULT must not read as INIT
    [InlineData("INITIALIZING")] // starts with INIT, but is not INIT
    [InlineData("DISABLED")]     // the firmware's internal state, never a reply value
    public async Task GetWifiLinkStateAsync_UnknownMnemonic_ThrowsWithTheReply(string reply)
    {
        // Compared whole: INIT and INITFAULT share a prefix and mean opposite things, so a
        // prefix match would report a module that will never recover as one about to come up.
        var device = ConnectedDevice(reply);

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetWifiLinkStateAsync());

        Assert.Equal(ConnectedQuery, ex.Command);
        Assert.Equal(reply, ex.DeviceResponse);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_NoReply_ThrowsWithNothingToReport()
    {
        var device = ConnectedDevice();

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetWifiLinkStateAsync());

        Assert.Null(ex.ErrorCode);
        Assert.Null(ex.DeviceResponse);
    }

    #endregion

    #region GetWifiLinkStateAsync — errors

    [Theory]
    [InlineData(WifiNotReadyOnTheWire)]
    [InlineData("**ERROR: -200, \"SYST:COMM:LAN: WiFi not ready (device powered up?)\"")]
    public async Task GetWifiLinkStateAsync_WifiNotReadyMinus200_ReturnsUnavailableRatherThanThrowing(string refusal)
    {
        // WiFi disabled or the device in standby: an expected answer, reported as a value. Keyed
        // on the code alone, so the text the firmware happens to print does not matter.
        var device = ConnectedDevice(refusal);

        Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_WifiNotReadyWithAnEmptyQueue_RemovesItsOwnMinus200()
    {
        // The device queues the -200 it volunteers. Reported as a value, it must not be left
        // behind: on the bench, three standby polls left three -200s for the next reader of the
        // queue (stream-start confirmation) to mistake for its own refusal. The count proves the
        // queue holds only this query's entry, so popping it is safe.
        var errorQueue = new Queue<string>();
        var device = StandbyDevice(errorQueue);

        Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());

        Assert.Empty(errorQueue);
        Assert.Equal(new[] { ConnectedQuery, ErrorCountQuery, ErrorQueueQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_WifiNotReadyBehindAnOlderError_LeavesTheQueueUntouched()
    {
        // SYSTem:ERRor? pops the OLDEST entry. With an earlier -113 queued, a blind pop would
        // consume that -113 and leave this query's -200 behind. The query cannot reach its own
        // entry without destroying someone else's, so it leaves both, in order.
        var errorQueue = new Queue<string>(new[] { "-113,\"Undefined header\"" });
        var device = StandbyDevice(errorQueue);

        Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());

        Assert.Equal(new[] { "-113,\"Undefined header\"", StandbyQueueEntry }, errorQueue);
        Assert.Equal(new[] { ConnectedQuery, ErrorCountQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_RepeatedStandbyPolls_DoNotGrowTheQueue()
    {
        var errorQueue = new Queue<string>();
        var device = StandbyDevice(errorQueue);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());
        }

        Assert.Empty(errorQueue);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_WifiNotReadyWithAnUnreadableCount_LeavesTheQueueUntouched()
    {
        // No count, no proof of ownership: nothing is popped.
        var device = ConnectedDevice();
        device.Responder = command => command == ConnectedQuery ? new[] { WifiNotReadyOnTheWire } : Array.Empty<string>();

        Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());

        Assert.Equal(new[] { ConnectedQuery, ErrorCountQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_CompetingQueueReaderBetweenCountAndPop_CannotGetIn()
    {
        // The query, the count and the pop are three exchanges. Held only per exchange, the lock
        // would let another caller drain this poll's -200 and queue its own error after the count
        // read 1 — and the pop would then discard that caller's error. The link-state read holds
        // the operation lock across all three, so the competitor runs only after the pop.
        var errorQueue = new Queue<string>();
        var device = StandbyDevice(errorQueue);
        var popReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.BeforeExchange = async index =>
        {
            if (index == 3) // CONnected?, ERRor:COUNt?, then the pop
            {
                popReached.SetResult();
                await releasePop.Task.ConfigureAwait(false);
            }
        };

        var link = Task.Run(() => device.GetWifiLinkStateAsync());
        await popReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var competitor = Task.Run(async () =>
        {
            var drained = await device.DrainErrorQueueAsync();
            await device.QueryAsync(CompetingBadCommand);
            return drained;
        });
        await Task.Delay(100);
        Assert.False(competitor.IsCompleted, "The competing reader ran inside the link-state read.");

        releasePop.SetResult();
        await Task.WhenAll(link, competitor).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WifiLinkState.Unavailable, await link);
        Assert.Empty(await competitor);                           // the poll's -200 was already gone
        Assert.Equal(new[] { CompetingCallersError }, errorQueue); // and the competitor's error survives
    }

    [Fact]
    public async Task GetActiveWifiModeAsync_InStandby_ReadsNullWithoutTouchingTheErrorQueue()
    {
        // Bench, firmware 3.9.0, SYSTem:POWer:STATe 0: NETType? 1 answers 0 with no error.
        var device = ConnectedDevice("0");

        Assert.Null(await device.GetActiveWifiModeAsync());
        Assert.Equal(new[] { ActiveModeQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_OtherScpiError_ThrowsWithItsCode()
    {
        var device = ConnectedDevice("**ERROR: -350, \"Queue overflow\"");

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetWifiLinkStateAsync());

        Assert.Equal(-350, ex.ErrorCode);
        Assert.Equal(ConnectedQuery, ex.Command);
    }

    [Fact]
    public async Task GetWifiLinkStateAsync_UndefinedHeader_ThrowsFeatureNotSupported()
    {
        // A device reporting 3.9.0 whose firmware does not actually know the command: the wire
        // answer is authoritative, so it gets the typed gating exception (ADR 0001 backstop).
        var device = ConnectedDevice("**ERROR: -113, \"Undefined header\"");

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetWifiLinkStateAsync());

        Assert.Equal(DeviceFeature.WifiLinkStatus, ex.Feature);
    }

    #endregion

    #region GetActiveWifiModeAsync

    [Theory]
    [InlineData("1", WifiMode.ExistingNetwork)]
    [InlineData("4", WifiMode.SelfHosted)]
    public async Task GetActiveWifiModeAsync_RunningMode_MapsToWifiMode(string reply, WifiMode expected)
    {
        var device = ConnectedDevice(reply);

        var mode = await device.GetActiveWifiModeAsync();

        Assert.Equal(expected, mode);
        Assert.Equal(new[] { ActiveModeQuery }, device.SentCommands);
    }

    [Fact]
    public async Task GetActiveWifiModeAsync_NoModeUp_ReturnsNull()
    {
        var device = ConnectedDevice("0");

        Assert.Null(await device.GetActiveWifiModeAsync());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("STA")]
    public async Task GetActiveWifiModeAsync_UnknownReply_ThrowsWithTheReply(string reply)
    {
        var device = ConnectedDevice(reply);

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetActiveWifiModeAsync());

        Assert.Equal(reply, ex.DeviceResponse);
        Assert.Equal(ActiveModeQuery, ex.Command);
    }

    [Fact]
    public async Task GetActiveWifiModeAsync_ScpiError_ThrowsWithItsCode()
    {
        // NETType? has no WiFi-not-ready refusal, so even -200 is a failed query here.
        var device = ConnectedDevice("**ERROR: -224, \"Illegal parameter value\"");

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetActiveWifiModeAsync());

        Assert.Equal(-224, ex.ErrorCode);
    }

    [Fact]
    public async Task GetActiveWifiModeAsync_StagedValueFollowedByParameterNotAllowed_Throws()
    {
        // What firmware older than v3.9.0 sends back for NETType? 1: the staged mode, then -108
        // for the argument it did not read. The staged value must not pass for the active one.
        var device = ConnectedDevice("4", "**ERROR: -108, \"Parameter not allowed\"");

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.GetActiveWifiModeAsync());

        Assert.Equal(-108, ex.ErrorCode);
    }

    #endregion

    #region Feature gate and preconditions

    [Fact]
    public void RequirementTable_WifiLinkStatus_RequiresV3_9_0AndWifi()
    {
        var requirement = DeviceFeatureRequirements.For(DeviceFeature.WifiLinkStatus);

        Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), requirement.MinVersion);
        Assert.Null(requirement.Boards);
        Assert.Equal(HardwareRequirement.WiFi, requirement.Hardware);
    }

    [Theory]
    [InlineData("3.8.0", false)]
    [InlineData("3.9.0", true)]
    public void Supports_WifiLinkStatus_AcrossTheV3_9_0Boundary(string firmware, bool expected)
    {
        var device = new DaqifiDevice("TestDevice");
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Metadata.FirmwareVersion = firmware;

        Assert.Equal(expected, device.Supports(DeviceFeature.WifiLinkStatus));
    }

    [Theory]
    [InlineData("3.8.0")]
    [InlineData("")]
    public async Task BothQueries_BelowV3_9_0OrWithNoVersion_ThrowFeatureNotSupportedWithoutSending(string firmware)
    {
        var device = ConnectedOn(firmware);

        var linkEx = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetWifiLinkStateAsync());
        var modeEx = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetActiveWifiModeAsync());

        Assert.All(new[] { linkEx, modeEx }, ex =>
        {
            Assert.Equal(DeviceFeature.WifiLinkStatus, ex.Feature);
            Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), ex.RequiredVersion);
        });
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task BothQueries_OnADeviceWithoutWifi_ThrowFeatureNotSupportedWithoutSending()
    {
        var device = ConnectedDevice("CONNECTED");
        device.Metadata.Capabilities = new DeviceCapabilities { HasWiFi = false };

        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetWifiLinkStateAsync());
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => device.GetActiveWifiModeAsync());
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task BothQueries_Disconnected_ThrowDeviceNotConnectedRatherThanUnsupported()
    {
        // Connectivity is checked before the gate: a disconnected device is reported as such,
        // not as lacking a feature its stale (here, too-old) version says it lacks.
        var device = new TestableWifiDevice { FirmwareVersionOnConnect = "3.8.0" };

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.GetWifiLinkStateAsync());
        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.GetActiveWifiModeAsync());
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task BothQueries_AlreadyCanceled_ThrowWithoutSending()
    {
        var device = ConnectedDevice("CONNECTED");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.GetWifiLinkStateAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.GetActiveWifiModeAsync(cts.Token));
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public void DaqifiStreamingDevice_ImplementsIWifiLinkStatusProvider()
    {
        Assert.IsAssignableFrom<IWifiLinkStatusProvider>(new TestableWifiDevice());
    }

    #endregion

    private static TestableWifiDevice ConnectedDevice(params string[] reply) => ConnectedOn("3.9.0", reply);

    /// <summary>
    /// A 3.9.0 device in standby with a FIFO SCPI error queue, answering the three commands the
    /// link-state query can send the way the bench Nq1 did: <c>CONnected?</c> queues a -200 and
    /// volunteers it inline, <c>SYSTem:ERRor:COUNt?</c> reports the depth without popping, and
    /// <c>SYSTem:ERRor?</c> pops the oldest entry.
    /// </summary>
    private static TestableWifiDevice StandbyDevice(Queue<string> errorQueue)
    {
        var device = ConnectedDevice();
        device.Responder = command =>
        {
            if (command == ConnectedQuery)
            {
                errorQueue.Enqueue(StandbyQueueEntry);
                return new[] { WifiNotReadyOnTheWire };
            }

            if (command == ErrorCountQuery)
            {
                return new[] { errorQueue.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }

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

    private static TestableWifiDevice ConnectedOn(string firmware, params string[] reply)
    {
        var device = new TestableWifiDevice();
        device.CannedTextResponse.AddRange(reply);
        device.Connect();
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Metadata.FirmwareVersion = firmware;
        return device;
    }

    /// <summary>
    /// A streaming device whose text exchange returns a canned reply, so the queries run through
    /// the real facade, collaborator and feature gate without a transport (mirrors
    /// <c>TestableLanChipInfoDevice</c>).
    /// </summary>
    private sealed class TestableWifiDevice : DaqifiStreamingDevice
    {
        public TestableWifiDevice() : base("TestDevice")
        {
        }

        public List<string> SentCommands { get; } = new();
        public List<string> CannedTextResponse { get; } = new();

        /// <summary>
        /// Computes the reply from the command the exchange sent; when unset, every exchange gets
        /// <see cref="CannedTextResponse"/>.
        /// </summary>
        public Func<string, string[]>? Responder { get; set; }

        private List<string> NextResponse()
            => Responder != null ? Responder(SentCommands[^1]).ToList() : CannedTextResponse.ToList();

        /// <summary>Firmware version to report without connecting, for the disconnected case.</summary>
        public string FirmwareVersionOnConnect
        {
            set
            {
                Metadata.DeviceType = DeviceType.Nyquist1;
                Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
                Metadata.FirmwareVersion = value;
            }
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        /// <summary>
        /// Runs before each exchange asks for the operation lock, with the exchange's 1-based
        /// index — the gap between two exchanges a competing caller would have to use.
        /// </summary>
        public Func<int, Task>? BeforeExchange { get; set; }

        private int _exchangeCount;

        /// <summary>A raw query through the same exchange, standing in for another caller's command.</summary>
        public Task<IReadOnlyList<string>> QueryAsync(string scpi)
            => ExecuteTextCommandAsync(() => Send(new ScpiMessage(scpi)));

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

            // Each exchange takes the device's operation lock, as the real text exchange does, and
            // like it runs nested when this flow already holds the lock (RunExclusiveAsync).
            return await RunExclusiveAsync(async ct =>
            {
                try
                {
                    if (prepareAsync != null)
                    {
                        await prepareAsync(ct).ConfigureAwait(false);
                    }

                    ct.ThrowIfCancellationRequested();
                    setupAction();
                    return (IReadOnlyList<string>)NextResponse();
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

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return NextResponse();
        }
    }
}
