using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network;

/// <summary>
/// Unit tests that drive <see cref="NetworkConfigurationOperations"/> directly through its
/// <see cref="IDeviceOperationHost"/> seam, rather than through the <see cref="DaqifiStreamingDevice"/>
/// facade it was extracted from (#344, final slice of #464).
/// </summary>
/// <remarks>
/// <para>
/// <c>NetworkConfigurableTests</c> already covers what each configuration command puts on the wire
/// and how the facade's local <see cref="NetworkConfiguration"/> snapshot ends up, driven end-to-end
/// through a testable <see cref="DaqifiStreamingDevice"/> subclass. That file is deliberately
/// untouched: it is the evidence the extraction changed nothing observable, and re-testing the same
/// ground here would only duplicate it.
/// </para>
/// <para>
/// What a direct test adds is what only the seam-level fake makes cheap to pin down: the exact
/// precedence between the argument-null check, the cancellation check and the connectivity check
/// (the collaborator checks cancellation <em>before</em> connectivity, the opposite of
/// <see cref="ConnectionGuard"/>'s default ordering); that streaming is stopped through
/// <c>StopStreaming()</c> rather than by writing <see cref="IDeviceOperationHost.IsStreaming"/>
/// directly, and only when it was actually active; and how far an invalid mode/security value lets
/// the send sequence run before the switch throws. The fake host throws
/// <see cref="NotSupportedException"/> for every member outside this collaborator's remit, so a
/// change that reaches for the channels lock, a text exchange or the SD/diagnostics surface fails
/// loudly instead of passing quietly.
/// </para>
/// </remarks>
public class NetworkConfigurationOperationsTests
{
    // Derived from ScpiMessageProducer rather than hardcoded, so a wire-format change there fails
    // this file's assertions instead of silently drifting out of sync with them.
    private static readonly string NetTypeExisting = ScpiMessageProducer.SetNetworkWifiModeExisting.Data;
    private static readonly string NetTypeSelfHosted = ScpiMessageProducer.SetNetworkWifiModeSelfHosted.Data;
    private static readonly string SecurityOpen = ScpiMessageProducer.SetNetworkWifiSecurityOpen.Data;
    private static readonly string SecurityWpa = ScpiMessageProducer.SetNetworkWifiSecurityWpa.Data;
    private static readonly string DisableSd = ScpiMessageProducer.DisableStorageSd.Data;
    private static readonly string EnableLan = ScpiMessageProducer.EnableNetworkLan.Data;
    private static readonly string SaveLan = ScpiMessageProducer.SaveNetworkLan.Data;
    private static readonly string ApplyLan = ScpiMessageProducer.ApplyNetworkLan.Data;
    private static readonly string LoadLan = ScpiMessageProducer.LoadNetworkLan.Data;
    private static readonly string FactoryResetLan = ScpiMessageProducer.FactoryResetNetworkLan.Data;

    /// <summary>
    /// Cancels <paramref name="cts"/> the instant LAN:APPLY is sent, so a test's call to
    /// <c>UpdateNetworkConfigurationAsync</c> skips the real
    /// <see cref="System.Threading.Tasks.Task.Delay">2-second module-restart wait</see> instead of
    /// paying it. The collaborator already treats a token that goes canceled during that wait as
    /// "the device already committed" and completes normally rather than throwing (see
    /// <see cref="UpdateNetworkConfigurationAsync_CanceledDuringTheRestartWait_CompletesAndUpdatesLocalState"/>),
    /// so this changes nothing about what a test using it observes — only how long it takes.
    /// </summary>
    private static void SkipRestartDelay(FakeHost host, CancellationTokenSource cts)
    {
        var previousHook = host.SendHook;
        host.SendHook = call =>
        {
            previousHook?.Invoke(call);
            if (call == "send:" + ApplyLan)
            {
                cts.Cancel();
            }
        };
    }

    #region Construction

    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NetworkConfigurationOperations(null!));
    }

    #endregion

    #region UpdateNetworkConfigurationAsync — check ordering

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_NullConfiguration_ThrowsWithoutTouchingTheHost()
    {
        // The argument-null check precedes both the cancellation check and the connectivity check,
        // so misuse reports the same exception regardless of either — even on a disconnected host
        // with an already-canceled token.
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ops.UpdateNetworkConfigurationAsync(null!, cts.Token));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_CanceledAndDisconnected_CancellationWinsOverConnectivity()
    {
        // Unlike ConnectionGuard's default (connected first, then cancellation), this method checks
        // the token before it ever asks the host whether it is connected.
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var config = ValidConfig();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ops.UpdateNetworkConfigurationAsync(config, cts.Token));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_Disconnected_ThrowsWithoutSending()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => ops.UpdateNetworkConfigurationAsync(ValidConfig()));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region UpdateNetworkConfigurationAsync — streaming

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_WhileStreaming_StopsViaTheHostMethodBeforeAnySend()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost { IsStreaming = true };
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);

        await ops.UpdateNetworkConfigurationAsync(ValidConfig(), cts.Token);

        Assert.Equal("stopstreaming", host.Calls.First());
        Assert.Equal(1, host.Calls.Count(c => c == "stopstreaming"));
        Assert.False(host.IsStreaming);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_NotStreaming_NeverCallsStopStreaming()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost { IsStreaming = false };
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);

        await ops.UpdateNetworkConfigurationAsync(ValidConfig(), cts.Token);

        Assert.DoesNotContain("stopstreaming", host.Calls);
    }

    #endregion

    #region UpdateNetworkConfigurationAsync — command sequence

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_ExistingNetworkWithStaticIP_SendsTheFullSequenceInOrder()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost();
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);
        var config = new NetworkConfiguration(
            WifiMode.ExistingNetwork,
            WifiSecurityType.WpaPskPhrase,
            "TestNetwork",
            "TestPassword",
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("255.255.255.0"),
            IPAddress.Parse("10.0.0.1"));

        await ops.UpdateNetworkConfigurationAsync(config, cts.Token);

        Assert.Equal(
            new[]
            {
                "send:" + NetTypeExisting,
                "send:" + ScpiMessageProducer.SetNetworkWifiSsid("TestNetwork").Data,
                "send:" + SecurityWpa,
                "send:" + ScpiMessageProducer.SetNetworkWifiPassword("TestPassword").Data,
                "send:" + ScpiMessageProducer.SetLanAddress(IPAddress.Parse("10.0.0.5")).Data,
                "send:" + ScpiMessageProducer.SetLanMask(IPAddress.Parse("255.255.255.0")).Data,
                "send:" + ScpiMessageProducer.SetLanGateway(IPAddress.Parse("10.0.0.1")).Data,
                "send:" + DisableSd,
                "send:" + EnableLan,
                "send:" + SaveLan,
                "send:" + ApplyLan,
            },
            host.Calls);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_SelfHostedOpenNetwork_SendsNoPassword()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost();
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);
        var config = new NetworkConfiguration(WifiMode.SelfHosted, WifiSecurityType.None, "DAQiFi_Device", "");

        await ops.UpdateNetworkConfigurationAsync(config, cts.Token);

        Assert.Equal(
            new[]
            {
                "send:" + NetTypeSelfHosted,
                "send:" + ScpiMessageProducer.SetNetworkWifiSsid("DAQiFi_Device").Data,
                "send:" + SecurityOpen,
                "send:" + DisableSd,
                "send:" + EnableLan,
                "send:" + SaveLan,
                "send:" + ApplyLan,
            },
            host.Calls);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_WithoutStaticIPFields_SkipsTheirSetters()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost();
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);

        await ops.UpdateNetworkConfigurationAsync(ValidConfig(), cts.Token);

        Assert.DoesNotContain(host.Calls, c => c.StartsWith("send:SYSTem:COMMunicate:LAN:ADDRess", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("send:SYSTem:COMMunicate:LAN:MASK", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("send:SYSTem:COMMunicate:LAN:GATEway", StringComparison.Ordinal));
    }

    #endregion

    #region UpdateNetworkConfigurationAsync — invalid enum values

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_UnsupportedMode_ThrowsBeforeSendingAnything()
    {
        // The mode switch is the very first send in the sequence, so an invalid mode must leave the
        // host untouched by any command (streaming is not active here, so not even the stop).
        var host = new FakeHost();
        var ops = new NetworkConfigurationOperations(host);
        var config = new NetworkConfiguration { Mode = (WifiMode)999, SecurityType = WifiSecurityType.None, Ssid = "x", Password = "" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ops.UpdateNetworkConfigurationAsync(config));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("send:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_UnsupportedSecurityType_ThrowsAfterModeAndSsidAreAlreadySent()
    {
        // The security switch runs after mode and SSID, so those two sends have already reached the
        // host by the time this throws — a fact the facade's exception-type test does not surface.
        var host = new FakeHost();
        var ops = new NetworkConfigurationOperations(host);
        var config = new NetworkConfiguration
        {
            Mode = WifiMode.SelfHosted,
            SecurityType = (WifiSecurityType)999,
            Ssid = "Test",
            Password = "",
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ops.UpdateNetworkConfigurationAsync(config));

        Assert.Equal(
            new[] { "send:" + NetTypeSelfHosted, "send:" + ScpiMessageProducer.SetNetworkWifiSsid("Test").Data },
            host.Calls);
    }

    #endregion

    #region UpdateNetworkConfigurationAsync — cancellation at the commit boundary

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_CanceledBeforeTheSave_AbortsWithoutSaveOrApply()
    {
        // Cancelling as the LAN-enable send goes out — the last command before the commit boundary —
        // is a clean abort: nothing has been persisted or applied yet.
        using var cts = new CancellationTokenSource();
        var host = new FakeHost { SendHook = call => { if (call == "send:" + EnableLan) cts.Cancel(); } };
        var ops = new NetworkConfigurationOperations(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ops.UpdateNetworkConfigurationAsync(ValidConfig(), cts.Token));

        Assert.DoesNotContain("send:" + SaveLan, host.Calls);
        Assert.DoesNotContain("send:" + ApplyLan, host.Calls);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_CanceledDuringTheRestartWait_CompletesAndUpdatesLocalState()
    {
        // Past the save/apply the device has already committed. An already-canceled token at that
        // point makes Task.Delay throw synchronously; the collaborator swallows it and still updates
        // the cached NetworkConfiguration, because reporting "canceled" here would be a lie about
        // what the device actually did.
        using var cts = new CancellationTokenSource();
        var host = new FakeHost { SendHook = call => { if (call == "send:" + ApplyLan) cts.Cancel(); } };
        var ops = new NetworkConfigurationOperations(host);
        var config = new NetworkConfiguration(
            WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, "NewNetwork", "NewPassword");

        await ops.UpdateNetworkConfigurationAsync(config, cts.Token);

        Assert.Contains("send:" + SaveLan, host.Calls);
        Assert.Contains("send:" + ApplyLan, host.Calls);
        Assert.Equal("NewNetwork", ops.NetworkConfiguration.Ssid);
    }

    #endregion

    #region NetworkConfiguration snapshot

    [Fact]
    public void NetworkConfiguration_ReturnsAFreshCloneEachTime()
    {
        var host = new FakeHost();
        var ops = new NetworkConfigurationOperations(host);

        var first = ops.NetworkConfiguration;
        var second = ops.NetworkConfiguration;
        first.Ssid = "Mutated";

        Assert.NotSame(first, second);
        Assert.NotEqual("Mutated", second.Ssid);
    }

    [Fact]
    public async Task UpdateNetworkConfigurationAsync_NullStaticFieldsOnASecondCall_PreserveThePreviouslyCachedValues()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost();
        SkipRestartDelay(host, cts);
        var ops = new NetworkConfigurationOperations(host);
        var staticIp = IPAddress.Parse("10.0.0.5");
        var subnet = IPAddress.Parse("255.255.255.0");
        var gateway = IPAddress.Parse("10.0.0.1");
        await ops.UpdateNetworkConfigurationAsync(new NetworkConfiguration(
            WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, "Net", "Pass", staticIp, subnet, gateway), cts.Token);

        // A fresh token: the first call already canceled the one above, and cancellation checked at
        // the top of the method must not reject this second, otherwise-independent call.
        using var cts2 = new CancellationTokenSource();
        SkipRestartDelay(host, cts2);
        await ops.UpdateNetworkConfigurationAsync(new NetworkConfiguration(
            WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, "OtherNet", "OtherPass"), cts2.Token);

        Assert.Equal(staticIp, ops.NetworkConfiguration.StaticIP);
        Assert.Equal(subnet, ops.NetworkConfiguration.SubnetMask);
        Assert.Equal(gateway, ops.NetworkConfiguration.Gateway);
    }

    #endregion

    #region LoadNetworkConfigurationAsync

    [Fact]
    public async Task LoadNetworkConfigurationAsync_CanceledAndDisconnected_CancellationWinsOverConnectivity()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ops.LoadNetworkConfigurationAsync(cts.Token));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task LoadNetworkConfigurationAsync_Disconnected_ThrowsWithoutSending()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => ops.LoadNetworkConfigurationAsync());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task LoadNetworkConfigurationAsync_Connected_SendsLoadOnly()
    {
        var host = new FakeHost();
        var ops = new NetworkConfigurationOperations(host);

        await ops.LoadNetworkConfigurationAsync();

        Assert.Equal(new[] { "send:" + LoadLan }, host.Calls);
    }

    #endregion

    #region FactoryResetNetworkAsync

    [Fact]
    public async Task FactoryResetNetworkAsync_CanceledAndDisconnected_CancellationWinsOverConnectivity()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ops.FactoryResetNetworkAsync(cts.Token));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task FactoryResetNetworkAsync_Disconnected_ThrowsWithoutSending()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new NetworkConfigurationOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => ops.FactoryResetNetworkAsync());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task FactoryResetNetworkAsync_Connected_SendsFactoryResetOnly()
    {
        var host = new FakeHost();
        var ops = new NetworkConfigurationOperations(host);

        await ops.FactoryResetNetworkAsync();

        Assert.Equal(new[] { "send:" + FactoryResetLan }, host.Calls);
    }

    #endregion

    private static NetworkConfiguration ValidConfig() => new(
        WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, "TestNetwork", "TestPassword");

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that records, in order, everything the network block is
    /// allowed to do to a device: send a command and stop streaming. Everything else throws, so a
    /// change that reaches further fails loudly.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<string> _calls = new();

        public IReadOnlyList<string> Calls => _calls;

        public bool IsConnected { get; set; } = true;
        public bool IsStreaming { get; set; }

        /// <summary>Runs after each call is recorded, so a test can trip cancellation at an exact point.</summary>
        public Action<string>? SendHook { get; set; }

        private void Record(string call)
        {
            _calls.Add(call);
            SendHook?.Invoke(call);
        }

        public void Send<T>(IOutboundMessage<T> message) => Record("send:" + message.Data);

        public void StopStreaming()
        {
            Record("stopstreaming");
            IsStreaming = false;
        }

        // Outside the network block's remit — reaching for any of these is a regression, not a
        // refinement.
        public void StartStreaming() => throw new NotSupportedException();
        public bool IsUsbConnection => throw new NotSupportedException();
        public int StreamingFrequency => throw new NotSupportedException();
        public DeviceMetadata Metadata => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public IReadOnlyList<IChannel> SnapshotChannels() => throw new NotSupportedException();
        public long ChannelStateVersion => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public TimeSpan SdCardDownloadTimeout => throw new NotSupportedException();
        public TimeSpan SdCardTransferIdleTimeout => throw new NotSupportedException();
        public void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }
}
