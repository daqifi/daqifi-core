using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Unit tests that drive <see cref="SdCardOperations"/> directly through its
/// <see cref="IDeviceOperationHost"/> seam, rather than through the
/// <see cref="DaqifiStreamingDevice"/> facade it was extracted from (#344, slice 4 of #464).
/// </summary>
/// <remarks>
/// <para>
/// <c>SdCardOperationsTests</c> already covers what each SD command puts on the wire and what the
/// device hands back, driven end-to-end through a testable <see cref="DaqifiStreamingDevice"/>
/// subclass. That file is deliberately untouched: it is the evidence that the extraction changed
/// nothing observable, and re-testing the same ground here would only duplicate it.
/// </para>
/// <para>
/// What a direct test adds is everything the facade's real text-exchange engine hides. Through the
/// facade an exchange is a black box, so these are invisible: how many exchanges an operation
/// actually performs (the retry budget, and which failures are deliberately <em>not</em> retried);
/// that the shared-SPI-bus switch is handed to the exchange as its <c>prepareAsync</c> phase and the
/// restore as its <c>finalizeAsync</c> phase rather than run inline around it (#407); that a device
/// which drops mid-exchange gets no restore written at it; that the listing asks for the longer
/// completion window; and, on the download path, that the single-download gate refuses a second
/// reader and that an abandoned transfer skips the LAN restore because its worker still owns the
/// transport (#399/#401).
/// </para>
/// <para>
/// The fake host below throws <see cref="NotSupportedException"/> for every member outside this
/// block's remit, so a change that reaches for the channels lock, the metadata, or the error-queue
/// drain fails loudly instead of passing quietly.
/// </para>
/// </remarks>
public class SdCardOperationsCollaboratorTests
{
    private static readonly byte[] EofMarker = Encoding.ASCII.GetBytes("__END_OF_FILE__");

    /// <summary>What <see cref="ParkedStream"/> delivers once the test releases it: one byte of file
    /// data followed by the EOF marker, so the held-open transfer completes as a normal download.</summary>
    private static readonly byte[] ReleasedPayload = Concat(new byte[] { 0x2A }, EofMarker);

    /// <summary>
    /// Upper bound for the two tests that park a transfer on a stream which ignores cancellation.
    /// The bound is only there so a regression that never returns fails the one test instead of
    /// hanging the run — it is not a performance assertion, so it is deliberately far above what
    /// these paths take.
    /// </summary>
    /// <remarks>
    /// The longest thing under it is the ~300 ms hard deadline the abandon test asserts; the rest
    /// (the entry signal, the gate's non-blocking answer, the release) are immediate. 10 s leaves
    /// about thirty times the headroom a loaded CI runner could plausibly need, while keeping the
    /// feedback on an actual regression to seconds rather than a minute.
    /// </remarks>
    private static readonly TimeSpan ParkedTestBudget = TimeSpan.FromSeconds(10);

    private const string DisableLan = "SYSTem:COMMunicate:LAN:ENAbled 0";
    private const string EnableLan = "SYSTem:COMMunicate:LAN:ENAbled 1";
    private const string EnableSd = "SYSTem:STORage:SD:ENAble 1";
    private const string DisableSd = "SYSTem:STORage:SD:ENAble 0";
    private const string ListFiles = "SYSTem:STORage:SD:LIST?";
    private const string SystemError = "SYSTem:ERRor?";
    private const string StopStreaming = "SYSTem:StopStreamData";

    private const string NoErrorTerminator = "0,\"No error\"";

    #region Construction

    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SdCardOperations(null!));
    }

    #endregion

    #region Interface handover

    [Fact]
    public void PrepareSdInterface_OverUsb_DropsLanBeforeTakingTheBus()
    {
        var host = new FakeHost { IsUsbConnection = true };
        var ops = new SdCardOperations(host);

        ops.PrepareSdInterface();

        Assert.Equal(new[] { "send:" + DisableLan, "send:" + EnableSd }, host.Calls);
    }

    [Fact]
    public void PrepareSdInterface_OverWifi_LeavesLanUp()
    {
        // #598/#599: the SD reply routes back over the very TCP link that asked for it, so the LAN
        // interface must survive the handover. Only the SD subsystem is enabled.
        var host = new FakeHost { IsUsbConnection = false };
        var ops = new SdCardOperations(host);

        ops.PrepareSdInterface();

        Assert.Equal(new[] { "send:" + EnableSd }, host.Calls);
    }

    [Fact]
    public void PrepareLanInterface_OverUsb_ReleasesTheBusAndBringsLanBack()
    {
        var host = new FakeHost { IsUsbConnection = true };
        var ops = new SdCardOperations(host);

        ops.PrepareLanInterface();

        Assert.Equal(new[] { "send:" + DisableSd, "send:" + EnableLan }, host.Calls);
    }

    [Fact]
    public void PrepareLanInterface_OverWifi_DoesNotReEnableLan()
    {
        // LAN was never disabled over WiFi, and LAN:ENAbled 1 re-initializes the WiFi module —
        // which would drop the link this command arrived on.
        var host = new FakeHost { IsUsbConnection = false };
        var ops = new SdCardOperations(host);

        ops.PrepareLanInterface();

        Assert.Equal(new[] { "send:" + DisableSd }, host.Calls);
    }

    [Fact]
    public void PrepareSdInterface_WhenDisconnected_ThrowsWithoutSending()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        Assert.Throws<DeviceNotConnectedException>(() => ops.PrepareSdInterface());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void PrepareLanInterface_WhenDisconnected_ThrowsWithoutSending()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        Assert.Throws<DeviceNotConnectedException>(() => ops.PrepareLanInterface());
        Assert.Empty(host.Calls);
    }

    #endregion

    #region GetSdCardFilesAsync

    [Fact]
    public async Task GetSdCardFilesAsync_RunsTheBusSwitchAndRestoreInsideTheExchange()
    {
        // The whole point of passing these as prepare/finalize phases rather than running them
        // around the call (#407): a competing exchange must not be able to interleave between the
        // switch and the LIST, nor between the LIST and the restore. Through the facade the
        // exchange is opaque; here the phase boundaries are visible.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/log_20240115_103000.bin 512", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.GetSdCardFilesAsync();

        Assert.Equal(
            new[]
            {
                "send:" + StopStreaming,
                "exchange:begin",
                "send:" + DisableLan,
                "send:" + EnableSd,
                "send:" + ListFiles,
                "send:" + SystemError,
                "send:" + DisableSd,
                "send:" + EnableLan,
                "exchange:end",
            },
            host.Calls);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_AsksForTheLongerCompletionWindow()
    {
        // The terminator can trail the last listing line by more than the 250ms default while the
        // firmware walks the directory tree, so the listing widens the inactivity window to 1s.
        // Only a direct test sees the arguments the exchange was opened with.
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.GetSdCardFilesAsync();

        Assert.Equal(3000, Assert.Single(host.ResponseTimeouts));
        Assert.Equal(1000, Assert.Single(host.CompletionTimeouts));
    }

    [Fact]
    public async Task GetSdCardFilesAsync_EmptyDirectoryWithTerminator_ReturnsEmptyRatherThanThrowing()
    {
        // The terminator is what makes "the card really is empty" distinguishable from "the reply
        // was lost" (#396).
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Empty(files);
        Assert.Equal(1, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_NoTerminator_RetriesExactlyOnceThenReportsIncomplete()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/a.bin 10");
        host.EnqueueResponse("Daqifi/a.bin 10");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardListIncompleteException>(() => ops.GetSdCardFilesAsync());

        Assert.Equal(2, host.ExchangeCount);
        Assert.Contains("Daqifi/a.bin 10", ex.RawDeviceResponse);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_TerminatorArrivesOnTheRetry_Succeeds()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/a.bin 10");
        host.EnqueueResponse("Daqifi/a.bin 10", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Equal(2, host.ExchangeCount);
        Assert.Equal("a.bin", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_TransientScpiError_RetriesEvenThoughTheReplyWasComplete()
    {
        // A terminated response that still carries a SCPI error is the interface-switch timing case
        // the retry exists for: complete, but not trustworthy.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200,\"Execution error\"", NoErrorTerminator);
        host.EnqueueResponse("Daqifi/a.bin 10", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Equal(2, host.ExchangeCount);
        Assert.Equal("a.bin", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_EveryAttemptHandsTheBusBackBeforeTheRetryDelay()
    {
        // The prepare/finalize pairing is per exchange, not per call, so the gap between attempts —
        // which is outside the exchange lock — is never one in which the device sits switched to
        // the card.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/a.bin 10");
        host.EnqueueResponse("Daqifi/a.bin 10", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.GetSdCardFilesAsync();

        Assert.Equal(2, host.Calls.Count(c => c == "send:" + EnableSd));
        Assert.Equal(2, host.Calls.Count(c => c == "send:" + DisableSd));
    }

    [Fact]
    public async Task GetSdCardFilesAsync_StaleTerminatorAheadOfTheListing_SplitsAtTheLastOne()
    {
        // A terminator reply left behind by a previous, timed-out exchange can lead this response.
        // Splitting at the FIRST match would discard the listing that follows and report an empty
        // card — the exact failure the terminator exists to prevent.
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator, "Daqifi/a.bin 10", "Daqifi/b.bin 20", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Equal(new[] { "a.bin", "b.bin" }, files.Select(f => f.FileName));
    }

    [Fact]
    public async Task GetSdCardFilesAsync_TerminatorShapedLineInsideTheListing_IsNotTreatedAsAFile()
    {
        // No firmware listing entry can be shaped like an error-queue reply — entries are always
        // "<path> <size>" — so a match inside the listing is another stale reply, not content.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/a.bin 10", "-200,\"Execution error\"", "Daqifi/b.bin 20", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Equal(new[] { "a.bin", "b.bin" }, files.Select(f => f.FileName));
    }

    [Fact]
    public async Task GetSdCardFilesAsync_DeviceDropsMidExchange_SkipsTheLanRestore()
    {
        // Nothing to restore once the link is gone: the sends would only throw
        // DeviceNotConnectedException over the top of whatever actually failed.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/a.bin 10", NoErrorTerminator);
        host.AfterExchangeSetup = _ => host.IsConnected = false;
        var ops = new SdCardOperations(host);

        var files = await ops.GetSdCardFilesAsync();

        Assert.Single(files);
        Assert.DoesNotContain("send:" + DisableSd, host.Calls);
        Assert.DoesNotContain("send:" + EnableLan, host.Calls);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_NoSdCard_ThrowsNotPresent()
    {
        var host = new FakeHost();
        host.EnqueueResponse("No SD Card Detected", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<SdCardNotPresentException>(() => ops.GetSdCardFilesAsync());
    }

    [Fact]
    public async Task GetSdCardFilesAsync_UnreadableDirectory_ThrowsFilesystemException()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Failed to open directory", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<SdCardFilesystemException>(() => ops.GetSdCardFilesAsync());
    }

    [Fact]
    public async Task GetSdCardFilesAsync_WhenDisconnected_ThrowsBeforeStoppingTheStream()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => ops.GetSdCardFilesAsync());
        Assert.Empty(host.Calls);
        Assert.Equal(0, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_OverWifiWithoutTheFeature_ThrowsBeforeTouchingTheWire()
    {
        var host = new FakeHost
        {
            IsUsbConnection = false,
            UnsupportedFeature = DeviceFeature.SdFileTransferOverWifi,
        };
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => ops.GetSdCardFilesAsync());

        Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
        Assert.Equal(new[] { "ensure:" + DeviceFeature.SdFileTransferOverWifi }, host.Calls);
        Assert.Equal(0, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardFilesAsync_OverUsb_NeverConsultsTheWifiFeatureGate()
    {
        // Over USB the operation is available on every SD-capable firmware; asking the gate would
        // make a USB listing fail on firmware that predates SD-over-WiFi for no reason.
        var host = new FakeHost { IsUsbConnection = true };
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.GetSdCardFilesAsync();

        Assert.DoesNotContain(host.Calls, c => c.StartsWith("ensure:", StringComparison.Ordinal));
    }

    #endregion

    #region GetSdCardStorageAsync

    [Fact]
    public async Task GetSdCardStorageAsync_ParsesFreeAndTotal()
    {
        var host = new FakeHost();
        host.EnqueueResponse("1048576000,2097152000");
        var ops = new SdCardOperations(host);

        var storage = await ops.GetSdCardStorageAsync();

        Assert.Equal(1048576000L, storage.FreeBytes);
        Assert.Equal(2097152000L, storage.TotalBytes);
        Assert.Equal(1, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardStorageAsync_NoCardMarker_IsNotRetried()
    {
        // "No SD Card Detected" is not a transient timing failure; retrying only delays the typed
        // exception and risks misclassification if the marker is absent second time round. The
        // exchange count is the only place that distinction is observable.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200,\"Execution error\"", "No SD Card Detected");
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<SdCardNotPresentException>(() => ops.GetSdCardStorageAsync());

        Assert.Equal(1, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardStorageAsync_TransientScpiError_RetriesOnceThenSucceeds()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200,\"Execution error\"");
        host.EnqueueResponse("1000,2000");
        var ops = new SdCardOperations(host);

        var storage = await ops.GetSdCardStorageAsync();

        Assert.Equal(1000L, storage.FreeBytes);
        Assert.Equal(2, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardStorageAsync_UndefinedHeader_ThrowsFeatureNotSupported()
    {
        // -113 means the firmware does not know the command at all (ADR 0001), so the device's own
        // answer — not the version seam — is what decides.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113,\"Undefined header\"");
        host.EnqueueResponse("**ERROR: -113,\"Undefined header\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => ops.GetSdCardStorageAsync());

        Assert.Equal(DeviceFeature.SdStorageQuery, ex.Feature);
        Assert.Equal(2, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardStorageAsync_UnparseableResponse_ThrowsOperationExceptionWithoutRetrying()
    {
        // Garbage with no SCPI error in it is not the transient case, so it must not spend a retry.
        var host = new FakeHost();
        host.EnqueueResponse("not a space report");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(() => ops.GetSdCardStorageAsync());

        Assert.Equal(1, host.ExchangeCount);
        Assert.Null(ex.LastScpiError);
    }

    [Fact]
    public async Task GetSdCardStorageAsync_WhileLogging_ThrowsBusy()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("busy.bin");

        await Assert.ThrowsAsync<SdCardBusyException>(() => ops.GetSdCardStorageAsync());
        Assert.Equal(0, host.ExchangeCount);
    }

    #endregion

    #region CheckSdCardSpaceAsync

    [Fact]
    public async Task CheckSdCardSpaceAsync_NearlyFull_RaisesTheWarningAndStillReturns()
    {
        // Advisory only: the warning fires but the caller is never blocked.
        var host = new FakeHost();
        host.EnqueueResponse("1024,2097152000");
        var ops = new SdCardOperations(host);

        var result = await ops.CheckSdCardSpaceAsync();

        Assert.True(result.IsNearlyFull);
        var warning = Assert.Single(host.LowSpaceWarnings);
        Assert.Same(result, warning.Result);
    }

    [Fact]
    public async Task CheckSdCardSpaceAsync_AmpleSpace_RaisesNothing()
    {
        var host = new FakeHost();
        host.EnqueueResponse("2000000000,2097152000");
        var ops = new SdCardOperations(host);

        var result = await ops.CheckSdCardSpaceAsync();

        Assert.False(result.ShouldWarn);
        Assert.Empty(host.LowSpaceWarnings);
    }

    #endregion

    #region SetSdCardMinimumFreeSpace

    [Fact]
    public void SetSdCardMinimumFreeSpace_NegativeOnADisconnectedDevice_ReportsTheArgument()
    {
        // Argument validation deliberately precedes the connection check, so misuse surfaces the
        // same exception type regardless of connection state. Which check wins is invisible unless
        // both would fire.
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ops.SetSdCardMinimumFreeSpace(-1));
        Assert.Equal("bytes", ex.ParamName);
    }

    [Fact]
    public void SetSdCardMinimumFreeSpace_Disconnected_Throws()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        Assert.Throws<DeviceNotConnectedException>(() => ops.SetSdCardMinimumFreeSpace(1024));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region Logging sessions

    [Fact]
    public async Task StartSdCardLoggingSessionAsync_OverWifi_RefusesRatherThanCuttingTheLink()
    {
        var host = new FakeHost { IsUsbConnection = false };
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.StartSdCardLoggingSessionAsync());
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(SdCardLogFormat.Protobuf, ".bin")]
    [InlineData(SdCardLogFormat.Json, ".json")]
    [InlineData(SdCardLogFormat.Csv, ".csv")]
    public async Task StartSdCardLoggingSessionAsync_AutoNamesWithTheFormatsExtension(
        SdCardLogFormat format, string expectedExtension)
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        var session = await ops.StartSdCardLoggingSessionAsync(format: format);

        Assert.Equal(format, session.Format);
        Assert.StartsWith("log_", session.FileName, StringComparison.Ordinal);
        Assert.EndsWith(expectedExtension, session.FileName, StringComparison.Ordinal);
        Assert.Contains("send:SYSTem:STORage:SD:FILE \"" + session.FileName + "\"", host.Calls);
    }

    [Fact]
    public async Task StartSdCardLoggingSessionAsync_QuotedFileName_IsRefused()
    {
        // The file name is interpolated into a SCPI command, so a quote would close the literal.
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<ArgumentException>(
            () => ops.StartSdCardLoggingSessionAsync("evil\";SYSTem:REboot.bin"));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task StartSdCardLoggingSessionAsync_NoChannelMask_LeavesTheCurrentConfigurationAlone()
    {
        var host = new FakeHost { StreamingFrequency = 250 };
        var ops = new SdCardOperations(host);

        await ops.StartSdCardLoggingSessionAsync("data.bin");

        Assert.DoesNotContain(host.Calls, c => c.StartsWith("send:ENAble:VOLTage:DC", StringComparison.Ordinal));
        Assert.Contains("send:SYSTem:StartStreamData 250", host.Calls);
        Assert.True(host.IsStreaming);
    }

    [Fact]
    public async Task StartSdCardLoggingSessionAsync_ChannelMask_EnablesItBeforeStreamingStarts()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        await ops.StartSdCardLoggingSessionAsync("data.bin", channelMask: "3");

        var calls = host.Calls.ToList();
        var maskIndex = calls.IndexOf("send:ENAble:VOLTage:DC 3");
        var startIndex = calls.FindIndex(c => c.StartsWith("send:SYSTem:StartStreamData", StringComparison.Ordinal));
        Assert.True(maskIndex >= 0, "the channel mask was never sent");
        Assert.True(maskIndex < startIndex, "the channel mask must be enabled before streaming starts");
    }

    [Fact]
    public async Task StopSdCardLoggingAsync_OverUsb_ReturnsTheStreamToUsbAndBringsLanBack()
    {
        var host = new FakeHost { IsUsbConnection = true };
        var ops = new SdCardOperations(host);

        await ops.StopSdCardLoggingAsync();

        Assert.Equal(
            new[]
            {
                "send:" + StopStreaming,
                "send:" + DisableSd,
                "send:SYSTem:STReam:INTerface 0",
                "send:" + EnableLan,
            },
            host.Calls);
        Assert.False(host.IsStreaming);
    }

    [Fact]
    public async Task StopSdCardLoggingAsync_OverWifi_DoesNotReEnableLan()
    {
        // #327: a session that logged over USB, disconnected, and came back over WiFi could
        // otherwise call this and cut itself off — LAN:ENAbled 1 re-initializes the WiFi module.
        var host = new FakeHost { IsUsbConnection = false };
        var ops = new SdCardOperations(host);

        await ops.StopSdCardLoggingAsync();

        Assert.Equal(new[] { "send:" + StopStreaming, "send:" + DisableSd }, host.Calls);
    }

    [Fact]
    public async Task StopSdCardLoggingAsync_ClearsTheBusyState()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("data.bin");
        Assert.True(ops.IsLoggingToSdCard);

        await ops.StopSdCardLoggingAsync();

        Assert.False(ops.IsLoggingToSdCard);
    }

    #endregion

    #region DeleteSdCardFileAsync

    [Fact]
    public async Task DeleteSdCardFileAsync_DeletesThenRelistsInTheSameExchange()
    {
        var host = new FakeHost();
        // The delete refresh now carries the same transport terminator the read
        // path uses, so the canned reply must too -- it is what proves the
        // exchange finished rather than being cut short.
        host.EnqueueResponse("Daqifi/keep.bin 10", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.DeleteSdCardFileAsync("gone.bin");

        Assert.Equal(
            new[]
            {
                "send:" + StopStreaming,
                "exchange:begin",
                "send:" + DisableLan,
                "send:" + EnableSd,
                "send:SYSTem:STORage:SD:DELete \"gone.bin\"",
                "send:" + ListFiles,
                "send:" + SystemError,
                "send:" + DisableSd,
                "send:" + EnableLan,
                "exchange:end",
            },
            host.Calls);
        Assert.Equal("keep.bin", Assert.Single(ops.SdCardFiles).FileName);
    }

    [Fact]
    public async Task DeleteSdCardFileAsync_ScpiError_RetriesExactlyOnce()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200,\"Execution error\"", NoErrorTerminator);
        host.EnqueueResponse("Daqifi/keep.bin 10", NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.DeleteSdCardFileAsync("gone.bin");

        Assert.Equal(2, host.ExchangeCount);
        Assert.Equal("keep.bin", Assert.Single(ops.SdCardFiles).FileName);
    }

    [Fact]
    public async Task DeleteSdCardFileAsync_QuotedFileName_IsRefusedBeforeTheWire()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<ArgumentException>(
            () => ops.DeleteSdCardFileAsync("evil\";SYSTem:REboot.bin"));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task DeleteSdCardFileAsync_WhileLogging_ThrowsBusy()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("busy.bin");

        await Assert.ThrowsAsync<SdCardBusyException>(() => ops.DeleteSdCardFileAsync("gone.bin"));
        Assert.Equal(0, host.ExchangeCount);
    }

    #endregion

    #region FormatSdCardAsync

    [Fact]
    public async Task FormatSdCardAsync_IsFireAndForget_WithNoTextExchange()
    {
        // Format has no reply to collect, so it deliberately does not open an exchange — and
        // therefore never switches the bus back either.
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        await ops.FormatSdCardAsync();

        Assert.Equal(
            new[] { "send:" + StopStreaming, "send:" + EnableSd, "send:SYSTem:STORage:SD:FORmat" },
            host.Calls);
        Assert.Equal(0, host.ExchangeCount);
    }

    #endregion

    #region DownloadSdCardFileAsync

    [Fact]
    public async Task DownloadSdCardFileAsync_HappyPath_WritesTheFileAndHandsTheBusBack()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var host = new FakeHost { RawCaptureStreamFactory = () => ScriptedStream.Of(Concat(payload, EofMarker)) };
        var ops = new SdCardOperations(host);
        using var destination = new MemoryStream();

        var result = await ops.DownloadSdCardFileAsync("data.bin", destination);

        Assert.Equal(payload.Length, result.FileSize);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("send:SYSTem:STORage:SD:GET \"data.bin\"", host.Calls);
        Assert.Contains("send:" + DisableSd, host.Calls);
        Assert.Contains("send:" + EnableLan, host.Calls);
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_ListingSaysTheFileIsEmpty_AcceptsTheZeroByteTransfer()
    {
        // #398 gap 2: a genuinely empty file is a legitimate 0-byte download, not a wedged SD
        // subsystem, so it must not spend the empty-transfer retry.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/empty.bin 0", NoErrorTerminator);
        var ops = new SdCardOperations(host);
        await ops.GetSdCardFilesAsync();

        host.RawCaptureStreamFactory = () => ScriptedStream.Of(EofMarker);
        using var destination = new MemoryStream();

        var result = await ops.DownloadSdCardFileAsync("empty.bin", destination);

        Assert.Equal(0, result.FileSize);
        Assert.Equal(1, host.Calls.Count(c => c == "send:SYSTem:STORage:SD:GET \"empty.bin\""));
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_TwoListedFilesShareAName_TreatsTheSizeAsUnknown()
    {
        // The listing keeps only the leaf name, so the same name can arrive from two directories.
        // An over-confident size there would wave through the very failure the empty-transfer guard
        // exists to catch, so the size resolves to "unknown" and the 0-byte transfer is retried.
        var host = new FakeHost();
        host.EnqueueResponse("Daqifi/dup.bin 0", "Logs/dup.bin 0", NoErrorTerminator);
        var ops = new SdCardOperations(host);
        await ops.GetSdCardFilesAsync();

        host.RawCaptureStreamFactory = () => ScriptedStream.Of(EofMarker, EofMarker);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<SdCardEmptyTransferException>(
            () => ops.DownloadSdCardFileAsync("dup.bin", destination));

        Assert.Equal(2, host.Calls.Count(c => c == "send:SYSTem:STORage:SD:GET \"dup.bin\""));
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_EmptyTransfer_ReissuesTheGetAndSucceedsOnTheRetry()
    {
        var payload = new byte[] { 9, 9, 9 };
        var host = new FakeHost
        {
            RawCaptureStreamFactory = () => ScriptedStream.Of(EofMarker, Concat(payload, EofMarker)),
        };
        var ops = new SdCardOperations(host);
        using var destination = new MemoryStream();

        var result = await ops.DownloadSdCardFileAsync("data.bin", destination);

        Assert.Equal(payload.Length, result.FileSize);
        Assert.Equal(2, host.Calls.Count(c => c == "send:SYSTem:STORage:SD:GET \"data.bin\""));
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_WhileAnotherIsParkedOnTheTransport_RefusesInsteadOfSharingIt()
    {
        // #399: a second reader on a stream an abandoned transfer still holds is the framing
        // corruption the device already refuses to risk elsewhere. Only reachable directly — the
        // facade offers no way to hold a download open.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeHost
        {
            RawCaptureStreamFactory = () => new ParkedStream(entered, release.Task),
        };
        var ops = new SdCardOperations(host);
        using var firstDestination = new MemoryStream();
        using var secondDestination = new MemoryStream();

        var first = ops.DownloadSdCardFileAsync("data.bin", firstDestination);
        try
        {
            await entered.Task.WaitAsync(ParkedTestBudget);

            // Bounded: the gate is supposed to answer immediately (it never blocks — it either
            // takes the semaphore or reports the state). A regression that made it wait instead
            // must fail this test, not hang the run.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ops.DownloadSdCardFileAsync("other.bin", secondDestination).WaitAsync(ParkedTestBudget));

            Assert.Contains("still in flight", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("send:SYSTem:STORage:SD:GET \"other.bin\"", host.Calls);
        }
        finally
        {
            release.TrySetResult(true);
            await first.WaitAsync(ParkedTestBudget);
        }
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_TransferThatIgnoresItsToken_IsAbandonedWithoutARestore()
    {
        // #399/#401: an abandoned worker is still alive and still owns the transport, so writing the
        // LAN restore at it would put SCPI onto a link a transfer is still reading. The caller is
        // told to reconnect; that re-establishes the interface anyway.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeHost
        {
            // 200ms budget → a ~300ms hard deadline (the grace is clamped to a 100ms floor).
            SdCardDownloadTimeout = TimeSpan.FromMilliseconds(200),
            RawCaptureStreamFactory = () => new ParkedStream(entered, release.Task),
        };
        var ops = new SdCardOperations(host);

        // Deliberately not disposed: the point of this test is that the worker is still running
        // when the call returns, so it may still touch this stream after the assertions. A
        // MemoryStream needs no disposal, and disposing it here would only manufacture a race.
        var destination = new MemoryStream();

        // Bounded well above the ~300ms hard deadline this test asserts. The stream deliberately
        // ignores its token, so if the deadline itself regressed the download would never return,
        // and an unbounded await would hang the whole run instead of failing the one test that
        // caught it. The bound is a token rather than WaitAsync(TimeSpan) on purpose: the latter
        // reports a TimeoutException, which is the very type this asserts, so a hang would have
        // passed as a success. A cancelled token surfaces as TaskCanceledException and fails.
        using var guard = new CancellationTokenSource(ParkedTestBudget);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => ops.DownloadSdCardFileAsync("data.bin", destination).WaitAsync(guard.Token));

            Assert.Contains("send:SYSTem:STORage:SD:GET \"data.bin\"", host.Calls);
            Assert.DoesNotContain("send:" + DisableSd, host.Calls);
            Assert.DoesNotContain("send:" + EnableLan, host.Calls);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_OverWifiWithoutTheFeature_ThrowsBeforeTouchingTheWire()
    {
        var host = new FakeHost
        {
            IsUsbConnection = false,
            UnsupportedFeature = DeviceFeature.SdFileTransferOverWifi,
        };
        var ops = new SdCardOperations(host);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.DownloadSdCardFileAsync("data.bin", destination));

        Assert.Equal(0, host.RawCaptureCount);
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_NullDestination_Throws()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ops.DownloadSdCardFileAsync("data.bin", (Stream)null!));
        Assert.Equal(0, host.RawCaptureCount);
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_QuotedFileName_IsRefusedBeforeTheWire()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ops.DownloadSdCardFileAsync("evil\";SYSTem:REboot.bin", destination));
        Assert.Equal(0, host.RawCaptureCount);
    }

    [Fact]
    public async Task DownloadSdCardFileAsync_WhileLogging_ThrowsBusy()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("busy.bin");
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<SdCardBusyException>(
            () => ops.DownloadSdCardFileAsync("data.bin", destination));
        Assert.Equal(0, host.RawCaptureCount);
    }

    #endregion

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    /// <summary>
    /// A read-only stream that hands back one scripted segment per read, so a test can script what
    /// each successive <c>SYSTem:STORage:SD:GET</c> sees. A single <see cref="MemoryStream"/> cannot:
    /// the receiver reads greedily, so it would swallow a later segment into the same read.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly IReadOnlyList<byte[]> _segments;
        private int _segmentIndex;
        private int _offsetInSegment;

        private ScriptedStream(IReadOnlyList<byte[]> segments) => _segments = segments;

        public static ScriptedStream Of(params byte[][] segments) => new(segments);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_segmentIndex >= _segments.Count)
            {
                return 0;
            }

            var segment = _segments[_segmentIndex];
            var length = Math.Min(count, segment.Length - _offsetInSegment);
            Buffer.BlockCopy(segment, _offsetInSegment, buffer, offset, length);

            // A caller whose buffer is smaller than the segment gets the rest on its next read
            // rather than losing the tail, which would make a fixture quietly mean something else.
            _offsetInSegment += length;
            if (_offsetInSegment >= segment.Length)
            {
                _segmentIndex++;
                _offsetInSegment = 0;
            }

            return length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose first read parks until the test releases it, deliberately ignoring the
    /// cancellation token — the software stand-in for a worker stuck in native serial I/O that no
    /// token can interrupt, which is the case the download's hard deadline exists to bound. Once
    /// released it delivers one byte and the EOF marker, so a transfer that was merely held open
    /// (rather than abandoned) still finishes cleanly instead of unwinding as a stall.
    /// </summary>
    private sealed class ParkedStream : Stream
    {
        private readonly TaskCompletionSource<bool> _entered;
        private readonly Task _release;
        private bool _delivered;

        public ParkedStream(TaskCompletionSource<bool> entered, Task release)
        {
            _entered = entered;
            _release = release;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _entered.TrySetResult(true);
            await _release.ConfigureAwait(false);

            if (_delivered)
            {
                return 0;
            }

            _delivered = true;
            var length = Math.Min(count, ReleasedPayload.Length);
            Buffer.BlockCopy(ReleasedPayload, 0, buffer, offset, length);
            return length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that records, in order, everything the SD block is
    /// allowed to do to a device: send a command, open a text exchange, run a raw capture, consult
    /// the firmware feature gate, and raise the low-space warning. Everything else throws, so a
    /// change that reaches further fails loudly.
    /// </summary>
    /// <remarks>
    /// The exchange reproduces the real engine's phase order — prepare, then the setup action, then
    /// finalize in a <c>finally</c> once the exchange has started — because several of the tests
    /// above assert exactly that ordering. The call log is guarded by a lock because a download
    /// records its sends from the worker thread the transfer runs on while the calling test is
    /// still reading them; the exchange bookkeeping needs no guard, since a text exchange only ever
    /// runs on the test's own flow.
    /// </remarks>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<string> _calls = new();
        private readonly Queue<IReadOnlyList<string>> _responses = new();

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) { return _calls.ToArray(); } }
        }

        public int ExchangeCount { get; private set; }
        public int RawCaptureCount { get; private set; }

        public bool IsConnected { get; set; } = true;
        public bool IsUsbConnection { get; set; } = true;
        public bool IsStreaming { get; set; } = true;
        public int StreamingFrequency { get; set; } = 100;

        public TimeSpan SdCardDownloadTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan SdCardTransferIdleTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>The one feature <see cref="EnsureSupported"/> refuses; null means everything is supported.</summary>
        public DeviceFeature? UnsupportedFeature { get; set; }

        public List<LowSdSpaceWarningEventArgs> LowSpaceWarnings { get; } = new();

        /// <summary>The response timeout each exchange asked for, in order.</summary>
        public List<int> ResponseTimeouts { get; } = new();

        /// <summary>The completion timeout each exchange asked for, in order.</summary>
        public List<int> CompletionTimeouts { get; } = new();

        /// <summary>
        /// Runs after the setup action of the nth exchange (1-based) and before its response is
        /// handed back, so a test can change device state at that exact point.
        /// </summary>
        public Action<int>? AfterExchangeSetup { get; set; }

        /// <summary>Supplies the stream a raw capture reads from. Defaults to an immediately-closed stream.</summary>
        public Func<Stream>? RawCaptureStreamFactory { get; set; }

        public void EnqueueResponse(params string[] lines)
        {
            lock (_responses) { _responses.Enqueue(lines); }
        }

        private void Record(string call)
        {
            lock (_calls) { _calls.Add(call); }
        }

        public void Send<T>(IOutboundMessage<T> message) => Record("send:" + message.Data);

        public void EnsureSupported(DeviceFeature feature)
        {
            Record("ensure:" + feature);
            if (UnsupportedFeature == feature)
            {
                throw new FeatureNotSupportedException(feature);
            }
        }

        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => new(feature);

        public void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e)
        {
            Record("lowspace");
            LowSpaceWarnings.Add(e);
        }

        public async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null)
        {
            var index = ++ExchangeCount;
            ResponseTimeouts.Add(responseTimeoutMs);
            CompletionTimeouts.Add(completionTimeoutMs);
            Record("exchange:begin");

            try
            {
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                setupAction();
                AfterExchangeSetup?.Invoke(index);

                lock (_responses)
                {
                    return _responses.Count > 0 ? _responses.Dequeue() : Array.Empty<string>();
                }
            }
            finally
            {
                if (finalizeAsync != null)
                {
                    await finalizeAsync().ConfigureAwait(false);
                }

                Record("exchange:end");
            }
        }

        public async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            RawCaptureCount++;
            Record("rawcapture");

            var stream = RawCaptureStreamFactory?.Invoke() ?? new MemoryStream();
            await rawAction(stream, cancellationToken).ConfigureAwait(false);
        }

        // Outside the SD block's remit — reaching for any of these is a regression, not a refinement.
        public DeviceMetadata Metadata => throw new NotSupportedException();
        public long ChannelStateVersion => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public IReadOnlyList<IChannel> SnapshotChannels() => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }
}
