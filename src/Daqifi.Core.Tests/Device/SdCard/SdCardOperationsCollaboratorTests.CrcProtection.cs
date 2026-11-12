using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// CRC-protected SD writes (<c>SYSTem:STORage:SD:PROTect</c>, firmware v3.9.0, issue #956),
/// driven through the same <see cref="FakeHost"/> as the rest of the collaborator tests.
/// </summary>
/// <remarks>
/// The replies below are the shapes the v3.9.0 firmware produces: a command that pushes an error
/// also writes it straight to the wire as <c>**ERROR: &lt;code&gt;, "..."</c>, and the
/// <c>SYSTem:ERRor?</c> terminator Core appends answers <c>&lt;code&gt;,"..."</c>.
/// </remarks>
public partial class SdCardOperationsCollaboratorTests
{
    private const string ProtectOn = "SYSTem:STORage:SD:PROTect 1";
    private const string ProtectOff = "SYSTem:STORage:SD:PROTect 0";
    private const string ProtectQuery = "SYSTem:STORage:SD:PROTect?";

    #region SetSdCardCrcProtectionAsync

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_On_SendsTheSetAndTheTerminatorInOneExchange()
    {
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);

        // No SD:ENAble and no LAN switch: the command needs SD storage OFF, and turning it on (as
        // the file operations' bus switch does) is exactly what the firmware refuses it for.
        Assert.Equal(
            new[]
            {
                "ensure:" + DeviceFeature.SdCardCrcProtection,
                "send:" + StopStreaming,
                "exchange:begin",
                "send:" + ProtectOn,
                "send:" + SystemError,
                "exchange:end",
            },
            host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_Off_SendsZero()
    {
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(false);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.Contains("send:" + ProtectOff, host.Calls);
        Assert.DoesNotContain("send:" + ProtectOn, host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_WaitsOutTheFirmwaresTwoSecondApplyWindow()
    {
        // The firmware holds its reply for up to 2 s while the driver drops the card for re-init,
        // and only then reports a change it could not apply. The default 1 s window would read
        // that as no answer at all.
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);

        Assert.True(Assert.Single(host.ResponseTimeouts) > 2000);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_BelowV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing()
    {
        var host = new FakeHost { UnsupportedFeature = DeviceFeature.SdCardCrcProtection };
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Equal(new[] { "ensure:" + DeviceFeature.SdCardCrcProtection }, host.Calls);
        Assert.Equal(0, host.ExchangeCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_NotConnected_ThrowsAndSendsNothing()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_WhileLogging_ThrowsBusyAndLeavesTheLogAlone()
    {
        // The device takes the change only with SD storage off. Making room by turning it off
        // would end the user's recording, so the call refuses instead, before sending anything:
        // not even the defensive stream stop the other SD operations open with.
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("session.bin");
        var callsBeforeTheSet = host.Calls
            .Append("ensure:" + DeviceFeature.SdCardCrcProtection)
            .ToArray();

        await Assert.ThrowsAsync<SdCardBusyException>(() => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(callsBeforeTheSet, host.Calls);
        Assert.Equal(0, host.ExchangeCount);
        Assert.True(host.IsLoggingToSdCard);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AfterTheLogStops_IsAccepted()
    {
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("session.bin");
        await ops.StopSdCardLoggingAsync();
        host.EnqueueResponse(NoErrorTerminator);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_SettingsConflict_ThrowsBusyWithTheDevicesReply()
    {
        // -221: SD storage is on or still busy on the device (a format still running, SD turned
        // on by another connection). The firmware did not store the value.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -221, \"Settings conflict\"", "-221,\"Settings conflict\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardBusyException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal("**ERROR: -221, \"Settings conflict\"", ex.LastScpiError);
        Assert.Contains("-221,\"Settings conflict\"", ex.RawDeviceResponse);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_StoredButNotApplied_ReturnsPending()
    {
        // -200 from this command means the value is stored and waits for the SD task to run
        // (standby, or WiFi streaming has the bus). It is a distinct outcome, not a failure.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200, \"Execution error\"", "-200,\"Execution error\"");
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Pending, result);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AnOlderErrorInTheQueue_IsNotTakenAsThisCommandsAnswer()
    {
        // The terminator pops the OLDEST queued error, which can belong to an earlier command.
        // Only an error line written by this command classifies it, so a stale -200 in the
        // terminator's reply must not turn an applied change into a pending one.
        var host = new FakeHost();
        host.EnqueueResponse("-200,\"Execution error\"");
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_NoAnswer_ThrowsOperationExceptionNotBusy()
    {
        // Silence says nothing about whether the value changed; it must not be reported as
        // applied, and it is not the device saying the card is in use either.
        var host = new FakeHost();
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.IsType<SdCardOperationException>(ex);
        Assert.Null(ex.LastScpiError);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_UndefinedHeader_ThrowsFeatureNotSupported()
    {
        // The backstop for a device whose reported version passed the gate but whose firmware
        // does not know the command.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113, \"Undefined header\"", "-113,\"Undefined header\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AnyOtherError_ThrowsOperationExceptionCarryingIt()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -224, \"Illegal parameter value\"", "-224,\"Illegal parameter value\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.IsType<SdCardOperationException>(ex);
        Assert.Equal("**ERROR: -224, \"Illegal parameter value\"", ex.LastScpiError);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_WasStreaming_ResumesTheStreamAfterwards()
    {
        var host = new FakeHost { IsStreaming = true };
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);

        Assert.True(host.IsStreaming);
        Assert.Equal(1, host.StartStreamingCallCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_Refused_StillResumesTheStream()
    {
        var host = new FakeHost { IsStreaming = true };
        host.EnqueueResponse("**ERROR: -221, \"Settings conflict\"", "-221,\"Settings conflict\"");
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<SdCardBusyException>(() => ops.SetSdCardCrcProtectionAsync(true));

        Assert.True(host.IsStreaming);
        Assert.Equal(1, host.StartStreamingCallCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_DoesNotHoldTheBus_SoALogCanStartAfterIt()
    {
        // #941: the call must leave the bus record as it found it. A log start after it is
        // admitted and runs its full sequence.
        var host = new FakeHost();
        host.EnqueueResponse(NoErrorTerminator);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);
        await ops.StartSdCardLoggingSessionAsync("after.bin");

        Assert.True(host.IsLoggingToSdCard);
    }

    #endregion

    #region GetSdCardCrcProtectionAsync

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(" 1 ", true)]
    public async Task GetSdCardCrcProtectionAsync_ReadsTheDevicesAnswer(string reply, bool expected)
    {
        var host = new FakeHost();
        host.EnqueueResponse(reply);
        var ops = new SdCardOperations(host);

        var enabled = await ops.GetSdCardCrcProtectionAsync();

        Assert.Equal(expected, enabled);
        Assert.Equal(
            new[]
            {
                "ensure:" + DeviceFeature.SdCardCrcProtection,
                "exchange:begin",
                "send:" + ProtectQuery,
                "exchange:end",
            },
            host.Calls);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_BelowV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing()
    {
        var host = new FakeHost { UnsupportedFeature = DeviceFeature.SdCardCrcProtection };
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.GetSdCardCrcProtectionAsync());

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Equal(0, host.ExchangeCount);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_DuringALog_AnswersWithoutStoppingIt()
    {
        // A plain query: no defensive stream stop, which during an SD log would end it.
        var host = new FakeHost();
        var ops = new SdCardOperations(host);
        await ops.StartSdCardLoggingSessionAsync("session.bin");
        var stopsBefore = host.Calls.Count(c => c == "send:" + StopStreaming);
        host.EnqueueResponse("1");

        var enabled = await ops.GetSdCardCrcProtectionAsync();

        Assert.True(enabled);
        Assert.True(host.IsLoggingToSdCard);
        Assert.Equal(stopsBefore, host.Calls.Count(c => c == "send:" + StopStreaming));
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_UnreadableAnswer_ThrowsOperationException()
    {
        var host = new FakeHost();
        host.EnqueueResponse("garbage");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(
            () => ops.GetSdCardCrcProtectionAsync());

        Assert.Contains("garbage", ex.RawDeviceResponse);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_ScpiError_ThrowsOperationExceptionCarryingIt()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200, \"Execution error\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(
            () => ops.GetSdCardCrcProtectionAsync());

        Assert.Equal("**ERROR: -200, \"Execution error\"", ex.LastScpiError);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_UndefinedHeader_ThrowsFeatureNotSupported()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113, \"Undefined header\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.GetSdCardCrcProtectionAsync());

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
    }

    #endregion
}
