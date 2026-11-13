using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// CRC-protected SD writes (<c>SYSTem:STORage:SD:PROTect</c>, firmware v3.9.0, issue #956),
/// driven through the same <see cref="FakeHost"/> as the rest of the collaborator tests.
/// </summary>
/// <remarks>
/// The replies below are the shapes the v3.9.0 firmware produces: a command that pushes an error
/// also writes it straight to the wire as <c>**ERROR: &lt;code&gt;, "..."</c>, the
/// <c>SYSTem:ERRor:COUNt?</c> terminator Core appends answers a bare count, and
/// <c>SYSTem:ERRor?</c> answers <c>&lt;code&gt;,"..."</c>.
/// </remarks>
public partial class SdCardOperationsCollaboratorTests
{
    private const string ProtectOn = "SYSTem:STORage:SD:PROTect 1";
    private const string ProtectOff = "SYSTem:STORage:SD:PROTect 0";
    private const string ProtectQuery = "SYSTem:STORage:SD:PROTect?";
    private const string SpaceQuery = "SYSTem:STORage:SD:SPACe?";
    private const string InfoQuery = "SYSTem:STORage:SD:INFO?";
    private const string ErrorCount = "SYSTem:ERRor:COUNt?";

    /// <summary>The readiness probe's reply when the card mounts: the bench card's free and total bytes, then an empty queue's count.</summary>
    private static readonly string[] CardReady = { "7797608448,7798738944", "0" };

    /// <summary>
    /// The readiness probe's reply when there is no card to mount, as firmware v3.9.0 sent it on
    /// the bench in the second after a change: the status text, the inline error, and a count
    /// showing that error is the only one queued.
    /// </summary>
    private static readonly string[] NoCard =
    {
        "Error !! No SD Card Detected",
        "**ERROR: -200, \"Execution error\"",
        "1",
    };

    /// <summary>What <c>SYSTem:ERRor?</c> answers when it pops a missing card's <c>-200</c>.</summary>
    private const string PoppedNoCard = "-200,\"Execution error\"";

    /// <summary><c>SYSTem:STORage:SD:INFO?</c> for a card initialized with CRC checking on.</summary>
    private const string CardInfoCrcOn = "3,\"SD\",\"SC64G\",8.0,305419896,2021-05,1,0";

    /// <summary><c>SYSTem:STORage:SD:INFO?</c> for a card initialized with CRC checking off.</summary>
    private const string CardInfoCrcOff = "3,\"SD\",\"SC64G\",8.0,305419896,2021-05,0,0";

    /// <summary>A change the device accepts, a card that mounts at once, and that card in <paramref name="crcOn"/>'s mode.</summary>
    private static void EnqueueAppliedChange(FakeHost host, bool crcOn)
    {
        host.EnqueueResponse("0");
        host.EnqueueResponse(CardReady);
        host.EnqueueResponse(crcOn ? CardInfoCrcOn : CardInfoCrcOff);
    }

    /// <summary>A missing card's probe and the pop of its own <c>-200</c>, <paramref name="times"/> over.</summary>
    private static void EnqueueEmptySlot(FakeHost host, int times)
    {
        for (var i = 0; i < times; i++)
        {
            host.EnqueueResponse(NoCard);
            host.EnqueueResponse(PoppedNoCard);
        }
    }

    #region SetSdCardCrcProtectionAsync

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_On_SetsMountsAndReadsTheModeBack()
    {
        var host = new FakeHost();
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);

        // No SD:ENAble and no LAN switch: the command needs SD storage OFF, and turning it on (as
        // the file operations' bus switch does) is exactly what the firmware refuses it for. And
        // no SYSTem:ERRor?: nothing failed, so nothing on the device's error queue is touched.
        Assert.Equal(
            new[]
            {
                "ensure:" + DeviceFeature.SdCardCrcProtection,
                "send:" + StopStreaming,
                "exchange:begin",
                "send:" + ProtectOn,
                "send:" + ErrorCount,
                "exchange:end",
                "exchange:begin",
                "send:" + SpaceQuery,
                "send:" + ErrorCount,
                "exchange:end",
                "exchange:begin",
                "send:" + InfoQuery,
                "exchange:end",
            },
            host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_Off_SendsZero()
    {
        var host = new FakeHost();
        EnqueueAppliedChange(host, crcOn: false);
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
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);

        Assert.True(host.ResponseTimeouts[0] > 2000);
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
        EnqueueAppliedChange(host, crcOn: true);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_SettingsConflict_ThrowsBusyWithTheDevicesReply()
    {
        // -221: SD storage is on or still busy on the device (a format still running, SD turned
        // on by another connection). The firmware did not store the value.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -221, \"Settings conflict\"", "1");
        host.EnqueueResponse("-221,\"Settings conflict\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardBusyException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal("**ERROR: -221, \"Settings conflict\"", ex.LastScpiError);
        Assert.Contains("**ERROR: -221, \"Settings conflict\"", ex.RawDeviceResponse);

        // The refusal was the only queued entry, so it is popped back off rather than left for
        // the next reader. Nothing else is sent: no card to wait for.
        Assert.Equal(2, host.ExchangeCount);
        Assert.Equal(1, host.Calls.Count(c => c == "send:" + SystemError));
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_StoredButNotApplied_ReturnsPending()
    {
        // -200 from this command means the value is stored and waits for the SD task to run
        // (standby, or WiFi streaming has the bus). It is a distinct outcome, not a failure.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200, \"Execution error\"", "1");
        host.EnqueueResponse(PoppedNoCard);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Pending, result);

        // The set and the pop of its own -200. The SD task is not running, so there is no card
        // to wait for.
        Assert.Equal(2, host.ExchangeCount);
        Assert.DoesNotContain("send:" + SpaceQuery, host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AnOlderErrorInTheQueue_IsLeftThereAndNotTakenAsThisCommandsAnswer()
    {
        // A count of one with no error line of this command's own is an entry from something
        // earlier. It neither turns an applied change into a refused one nor gets popped.
        var host = new FakeHost();
        host.EnqueueResponse("1");
        host.EnqueueResponse("7797608448,7798738944", "1");
        host.EnqueueResponse(CardInfoCrcOn);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.DoesNotContain("send:" + SystemError, host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_RefusedWithOlderErrorsQueued_LeavesTheQueueAlone()
    {
        // SYSTem:ERRor? pops the OLDEST entry. With two queued, popping would discard the older
        // one and leave this refusal behind, so neither is touched.
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -221, \"Settings conflict\"", "2");
        var ops = new SdCardOperations(host);

        await Assert.ThrowsAsync<SdCardBusyException>(() => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(1, host.ExchangeCount);
        Assert.DoesNotContain("send:" + SystemError, host.Calls);
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
        host.EnqueueResponse("**ERROR: -113, \"Undefined header\"", "1");
        host.EnqueueResponse("-113,\"Undefined header\"");
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AnyOtherError_ThrowsOperationExceptionCarryingIt()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -224, \"Illegal parameter value\"", "1");
        host.EnqueueResponse("-224,\"Illegal parameter value\"");
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
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);

        Assert.True(host.IsStreaming);
        Assert.Equal(1, host.StartStreamingCallCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_Refused_StillResumesTheStream()
    {
        var host = new FakeHost { IsStreaming = true };
        host.EnqueueResponse("**ERROR: -221, \"Settings conflict\"", "1");
        host.EnqueueResponse("-221,\"Settings conflict\"");
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
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        await ops.SetSdCardCrcProtectionAsync(true);
        await ops.StartSdCardLoggingSessionAsync("after.bin");

        Assert.True(host.IsLoggingToSdCard);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AChange_WaitsUntilTheCardCanBeMountedAgain()
    {
        // Bench, firmware v3.9.0: the device answers the set once it has dropped the card, and
        // the card cannot be mounted for up to a second after that. An SD log started in that
        // window found no card and wrote no file, silently. So Applied waits for a mount to work.
        var host = new FakeHost();
        host.EnqueueResponse("0");
        EnqueueEmptySlot(host, times: 2);
        host.EnqueueResponse(CardReady);
        host.EnqueueResponse(CardInfoCrcOn);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.Equal(3, host.Calls.Count(c => c == "send:" + SpaceQuery));

        // Each failed attempt's -200 was the only queued entry, so each is popped back off
        // rather than left for the next connect: one pop per failed attempt, none for the mount.
        Assert.Equal(2, host.Calls.Count(c => c == "send:" + SystemError));
        Assert.Equal(7, host.ExchangeCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AFailedAttemptWithOlderErrorsQueued_LeavesThemInPlace()
    {
        // The device's queue already holds two entries from earlier commands. A failed attempt's
        // -200 lands behind them, and SYSTem:ERRor? would pop the oldest of those instead, so
        // nothing is popped and their order is kept.
        var host = new FakeHost();
        host.EnqueueResponse("2");
        host.EnqueueResponse("Error !! No SD Card Detected", "**ERROR: -200, \"Execution error\"", "3");
        host.EnqueueResponse("7797608448,7798738944", "3");
        host.EnqueueResponse(CardInfoCrcOn);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.DoesNotContain("send:" + SystemError, host.Calls);
        Assert.Equal(4, host.ExchangeCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_ACardThatCameBackInTheOtherMode_Throws()
    {
        // The device keeps one setting. Something other than this call (another connection, a
        // raw command) turned protection off again while the card was re-initializing, so the
        // card mounts unprotected: a mount alone would have reported this call's change applied.
        var host = new FakeHost();
        host.EnqueueResponse("0");
        host.EnqueueResponse(CardReady);
        host.EnqueueResponse(CardInfoCrcOff);
        var ops = new SdCardOperations(host);

        var ex = await Assert.ThrowsAsync<SdCardOperationException>(
            () => ops.SetSdCardCrcProtectionAsync(true));

        Assert.IsType<SdCardOperationException>(ex);
        Assert.Contains(CardInfoCrcOff, ex.RawDeviceResponse);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_AnUnreadableModeReply_MountsAndReadsAgain()
    {
        var host = new FakeHost();
        host.EnqueueResponse("0");
        host.EnqueueResponse(CardReady);
        host.EnqueueResponse("garbage");
        host.EnqueueResponse(CardReady);
        host.EnqueueResponse(CardInfoCrcOn);
        var ops = new SdCardOperations(host);

        Assert.Equal(SdCardCrcProtectionResult.Applied, await ops.SetSdCardCrcProtectionAsync(true));
        Assert.Equal(5, host.ExchangeCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_TwoCallsAtOnce_TheSecondWaitsForTheFirstsConfirmation()
    {
        // Without serializing, an opposite call could send its set while the first was waiting
        // for the card, and the first would then confirm against a card in the other mode.
        var host = new FakeHost();
        using var firstIsMounting = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        host.AfterExchangeSetup = index =>
        {
            if (index == 2)
            {
                firstIsMounting.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };
        EnqueueAppliedChange(host, crcOn: true);
        EnqueueAppliedChange(host, crcOn: false);
        var ops = new SdCardOperations(host);

        var first = Task.Run(() => ops.SetSdCardCrcProtectionAsync(true));
        Assert.True(firstIsMounting.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => ops.SetSdCardCrcProtectionAsync(false));

        // Can only pass wrongly, never fail wrongly: if the second call is let through, its set
        // goes out well inside this window.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain("send:" + ProtectOff, host.Calls);

        releaseFirst.Set();
        Assert.Equal(SdCardCrcProtectionResult.Applied, await first);
        Assert.Equal(SdCardCrcProtectionResult.Applied, await second);

        var calls = host.Calls.ToList();
        Assert.True(calls.IndexOf("send:" + InfoQuery) < calls.IndexOf("send:" + ProtectOff));
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_QueuedWhileALogStarts_RefusesWithoutStoppingTheLog()
    {
        // The second call passes its log check while the first is still mounting, then waits. A
        // log start that arrived in between claims the bus once the first is confirmed. Without a
        // second check after the wait, the second call's defensive stream stop would end that log.
        var host = new FakeHost();
        using var firstIsMounting = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        host.AfterExchangeSetup = index =>
        {
            if (index == 2)
            {
                firstIsMounting.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        var first = Task.Run(() => ops.SetSdCardCrcProtectionAsync(true));
        Assert.True(firstIsMounting.Wait(TimeSpan.FromSeconds(10)));
        var log = ops.StartSdCardLoggingSessionAsync("queued.bin");
        var second = ops.SetSdCardCrcProtectionAsync(false);
        Assert.False(log.IsCompleted, "The log start claimed the bus during the first change.");
        Assert.False(second.IsCompleted);

        releaseFirst.Set();
        Assert.Equal(SdCardCrcProtectionResult.Applied, await first);
        await Assert.ThrowsAsync<SdCardBusyException>(() => second);
        await log;

        var calls = host.Calls.ToList();
        var firstConfirmed = calls.IndexOf("send:" + InfoQuery);
        Assert.True(firstConfirmed < calls.IndexOf("send:" + ScpiMessageProducer.DisableNetworkLan.Data));
        Assert.DoesNotContain("send:" + StopStreaming, calls.Skip(firstConfirmed));
        Assert.DoesNotContain("send:" + ProtectOff, calls);
        Assert.True(host.IsLoggingToSdCard);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_TurningProtectionOffWithAnEmptySlot_ReturnsPending()
    {
        // SD:INFO? alone would not catch this: it answers from cached state, and a fresh empty
        // slot reports CRC 0, which matches a request to turn protection off. The mount does not.
        var clock = new FakeTimeProvider();
        var host = new FakeHost { TimeProvider = clock };
        host.AfterExchangeSetup = _ => clock.Advance(TimeSpan.FromSeconds(1));
        host.EnqueueResponse("0");
        EnqueueEmptySlot(host, times: 10);
        var ops = new SdCardOperations(host);

        var result = await ops.SetSdCardCrcProtectionAsync(false);

        Assert.Equal(SdCardCrcProtectionResult.Pending, result);

        // Bounded by the four-second budget on a clock that steps a second per exchange: the
        // set, then two attempts, each followed by the pop of its own -200.
        Assert.Equal(5, host.ExchangeCount);
        Assert.DoesNotContain("send:" + InfoQuery, host.Calls);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_TheValueAlreadyHeldWithTheCardRemoved_ReturnsPending()
    {
        // The device answers an unchanged value at once and re-initializes nothing, and SD:INFO?
        // would still report the removed card's mode. The readiness check runs anyway, so a
        // removed card is Pending, not Applied.
        var clock = new FakeTimeProvider();
        var host = new FakeHost { TimeProvider = clock };
        host.AfterExchangeSetup = _ => clock.Advance(TimeSpan.FromSeconds(1));
        host.EnqueueResponse("0");
        EnqueueEmptySlot(host, times: 10);
        var ops = new SdCardOperations(host);

        Assert.Equal(SdCardCrcProtectionResult.Pending, await ops.SetSdCardCrcProtectionAsync(true));
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_TheValueAlreadyHeldWithACardPresent_TakesOneMount()
    {
        var host = new FakeHost();
        EnqueueAppliedChange(host, crcOn: true);
        var ops = new SdCardOperations(host);

        Assert.Equal(SdCardCrcProtectionResult.Applied, await ops.SetSdCardCrcProtectionAsync(true));

        // The set, one mount, one mode read.
        Assert.Equal(3, host.ExchangeCount);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_MountNeverAnswers_ReturnsPendingAtTheBudget()
    {
        var clock = new FakeTimeProvider();
        var host = new FakeHost { TimeProvider = clock };
        host.AfterExchangeSetup = _ => clock.Advance(TimeSpan.FromSeconds(1));
        host.EnqueueResponse("0");

        var ops = new SdCardOperations(host);

        Assert.Equal(SdCardCrcProtectionResult.Pending, await ops.SetSdCardCrcProtectionAsync(true));
        Assert.DoesNotContain("send:" + SystemError, host.Calls);
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
