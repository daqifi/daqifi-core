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

    /// <summary>The firmware's exact refusal for a disabled or powered-down radio.</summary>
    private const string WifiNotReady = "**ERROR: -200, \"SYST:COMM:LAN: WiFi not ready (device powered up?)\"";

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

    [Fact]
    public async Task GetWifiLinkStateAsync_WifiNotReadyMinus200_ReturnsUnavailableRatherThanThrowing()
    {
        // WiFi disabled or the device in standby: an expected answer, reported as a value.
        var device = ConnectedDevice(WifiNotReady);

        Assert.Equal(WifiLinkState.Unavailable, await device.GetWifiLinkStateAsync());
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

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            try
            {
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
