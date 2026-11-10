using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceInitializeTests
{
    /// <summary>
    /// The status message a working device answers <c>SYSInfoPB?</c> with: its channels AND
    /// which device it is. Both halves matter — a connect rejects a status that reports channels
    /// and cannot identify the device, because every value derived from it would be a guess
    /// (issue #922).
    /// </summary>
    /// <remarks>
    /// The doubles below hand this to <c>OnStatusMessageReceived</c>, which is the method the
    /// protobuf protocol handler calls on a real connection, so a test exercises the same path
    /// the device drives rather than the channel-population step alone.
    /// </remarks>
    private static DaqifiOutMessage HealthyStatus() => new()
    {
        DevicePn = "Nq1",
        DeviceSn = 9090539562006014104,
        DeviceFwRev = "3.8.0",
        AnalogInPortNum = 2,
        DigitalPortNum = 2
    };

    [Fact]
    public async Task InitializeAsync_SendsAllConfigCommands()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert — the 4 text commands are sent via ExecuteTextCommandAsync
        // and GetDeviceInfo is sent via direct Send()
        var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();

        Assert.Contains(sentData, d => d.Contains("SYSTem:ECHO -1"));
        Assert.Contains(sentData, d => d.Contains("SYSTem:StopStreamData"));
        Assert.Contains(sentData, d => d.Contains("SYSTem:POWer:STATe 1"));
        Assert.Contains(sentData, d => d.Contains("SYSTem:STReam:FORmat 0"));
        Assert.Contains(sentData, d => d.Contains("SYSTem:SYSInfoPB?"));

        // The stream-disruptive commands are the default because this session is assumed to
        // own the device; the opt-out below must not change that (#385).
        Assert.False(device.PreserveActiveStream);
    }

    [Fact]
    public async Task InitializeAsync_WithPreserveActiveStream_OmitsStreamDisruptiveCommands()
    {
        // Arrange — a secondary session attaching to a device another session may already be
        // streaming from.
        var device = new TestableDaqifiDevice("TestDevice") { PreserveActiveStream = true };
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert — nothing that halts, powers, or reconfigures the device's single global
        // stream is sent.
        var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();

        Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:StopStreamData"));
        Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:POWer:STATe"));
        Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:STReam:FORmat"));

        // ...but the session is still fully set up: echo off so its own replies parse, and the
        // identity query that populates channels.
        Assert.Contains(sentData, d => d.Contains("SYSTem:ECHO -1"));
        Assert.Contains(sentData, d => d.Contains("SYSTem:SYSInfoPB?"));
    }

    [Fact]
    public async Task InitializeAsync_WithPreserveActiveStream_StillProducesReadyPopulatedDevice()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice") { PreserveActiveStream = true };
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert — skipping the disruptive commands must not cost the caller a usable session
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count); // 2 analog + 2 digital
    }

    [Fact]
    public async Task InitializeAsync_WithPreserveActiveStream_StreamingUsb_DoesNotRouteStreamToUsb()
    {
        // Arrange — over USB the streaming device normally claims the stream with
        // SYSTem:STReam:INTerface 0, which would take data away from a session already
        // receiving it over WiFi.
        var device = new TestableStreamingDevice("TestDevice") { PreserveActiveStream = true };
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert
        Assert.DoesNotContain(device.SentData, d => d.Contains("SYSTem:STReam:INTerface"));
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
    }

    [Fact]
    public async Task InitializeAsync_WhenPreserveActiveStreamChangesMidInitialization_KeepsTheDecisionItStartedWith()
    {
        // Arrange — an observing initialization whose flag is flipped to false after it has
        // begun (a concurrent initialization on the same instance, or a caller mutating the
        // property). The decision belongs to the operation, so the USB routing step must still
        // be skipped: honoring the late change would steal a stream this session promised not
        // to touch.
        var device = new TestableStreamingDevice("TestDevice") { PreserveActiveStream = true };
        device.MutateDuringInitialization = () => device.PreserveActiveStream = false;
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert
        Assert.False(device.PreserveActiveStream); // the mutation really did land
        Assert.DoesNotContain(device.SentData, d => d.Contains("SYSTem:STReam:INTerface"));
        Assert.Equal(DeviceState.Ready, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenPreserveActiveStreamIsSetMidInitialization_StillTakesControl()
    {
        // Arrange — the mirror case: a normal take-control initialization must not be silently
        // downgraded to observing by a flag set after it started, which would leave the stream
        // routed somewhere this session cannot read.
        var device = new TestableStreamingDevice("TestDevice");
        device.MutateDuringInitialization = () => device.PreserveActiveStream = true;
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert
        Assert.True(device.PreserveActiveStream); // the mutation really did land
        Assert.Contains(device.SentData, d => d.Contains("SYSTem:STReam:INTerface 0"));
        Assert.Contains(device.SentData, d => d.Contains("SYSTem:StopStreamData"));
        Assert.Equal(DeviceState.Ready, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlreadyInitialized_DoesNotSendCommandsAgain()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();
        await device.InitializeAsync();
        var firstCallCount = device.DirectSentMessages.Count;

        // Act — second call
        await device.InitializeAsync();

        // Assert — no additional commands sent on second call
        Assert.Equal(firstCallCount, device.DirectSentMessages.Count);
    }

    [Fact]
    public async Task InitializeAsync_WhenDisconnected_Throws()
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        // Not connected

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.InitializeAsync());
        Assert.Equal("Device must be connected before initialization.", ex.Message);
    }

    [Fact]
    public async Task InitializeAsync_WhenDeviceReturnsScpiError_ThrowsTypedException()
    {
        // Arrange — the device answers the setup block with a -200 error line.
        var device = new TestableDaqifiDevice("TestDevice",
            textCommandResponse: new[] { "**ERROR: -200, \"Execution error\"\r\n" });
        device.Connect();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
            () => device.InitializeAsync());

        Assert.Contains("-200", ex.Message);
        Assert.Equal("**ERROR: -200, \"Execution error\"", ex.LastScpiError);
        Assert.Equal(DeviceState.Error, device.State);
        // The block is sent once. The retry it used to carry existed to get past a stale queue
        // entry, and the queue is now emptied before the block runs (#898).
        Assert.Equal(1, device.TextCommandAttemptCount);
    }

    [Theory]
    [InlineData("ERROR -200,\"Execution error\"\r\n")]
    [InlineData("ERROR\t-200,\"Execution error\"\r\n")]
    [InlineData("ERROR: -200,\"Execution error\"\r\n")]
    public async Task InitializeAsync_WhenDeviceReturnsBareErrorWithNonColonDelimiter_ThrowsTypedException(string errorLine)
    {
        // Arrange — a bare "ERROR" token (no "**" prefix) using a space/tab/colon delimiter
        // rather than "**ERROR" must still be recognized as a real SCPI error.
        var device = new TestableDaqifiDevice("TestDevice",
            textCommandResponse: new[] { errorLine });
        device.Connect();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
            () => device.InitializeAsync());

        Assert.Contains("-200", ex.Message);
        Assert.Equal(DeviceState.Error, device.State);
    }


    [Fact]
    public async Task InitializeAsync_EndsTheSetupSequenceWithTheErrorQueueQuery()
    {
        // Issue #667 item 2. None of the four setup commands answers unless something goes
        // wrong, so the text exchange had nothing to complete on and waited out its whole
        // 1000ms response timeout on every connect. The error-queue query gives it a reply to
        // finish on — the same terminator the SD directory listing uses (#396) — so it has to
        // be sent, and sent last, or it terminates nothing.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        await device.InitializeAsync();

        var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();
        // The last one: the drain that now runs before the block reads the queue too (#898).
        var terminatorIndex = sentData.FindLastIndex(d => d.Contains("SYSTem:ERRor?"));
        var lastSetupIndex = sentData.FindIndex(d => d.Contains("SYSTem:STReam:FORmat 0"));

        Assert.True(terminatorIndex >= 0, "The init sequence must query the error queue.");
        Assert.True(
            terminatorIndex > lastSetupIndex,
            "The terminator must follow the setup commands, or it cannot report on them.");
    }

    [Fact]
    public async Task InitializeAsync_WhenErrorQueueReportsNoError_SucceedsWithoutRetrying()
    {
        // The terminator's ordinary reply on a healthy device. It is emphatically not a
        // failure, and it contains the word "error" — so a check that matched on message text
        // rather than parsing the code would make every single connect retry and then throw.
        var device = new TestableDaqifiDevice("TestDevice",
            textCommandResponse: new[] { "0,\"No error\"\r\n" });
        device.Connect();

        await device.InitializeAsync();

        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(1, device.TextCommandAttemptCount);
    }

    [Theory]
    [InlineData("-200,\"Execution error\"\r\n")]
    [InlineData("-113,\"Undefined header\"\r\n")]
    public async Task InitializeAsync_WhenErrorQueueReportsAFailure_ThrowsTypedException(
        string errorReply)
    {
        // The error-queue reply carries a bare code with no ERROR token, so IsScpiErrorLine
        // does not match it — before #667 item 2 added the query, a setup command that failed
        // silently (recorded in the queue rather than volunteered) was never noticed at all.
        var device = new TestableDaqifiDevice("TestDevice",
            textCommandResponse: new[] { errorReply });
        device.Connect();

        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
            () => device.InitializeAsync());

        Assert.Equal(errorReply.Trim(), ex.LastScpiError);
        Assert.Equal(DeviceState.Error, device.State);
        Assert.Equal(1, device.TextCommandAttemptCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InitializeAsync_WhenTheErrorQueueHoldsStaleEntries_StillConnects(int staleEntries)
    {
        // Issue #898. The entries are left over from an earlier session: the device keeps its
        // error queue across a disconnect. The setup block's SYSTem:ERRor? pops one entry, so
        // before the fix a device carrying two or more of them answered that query with
        // somebody else's error and every connect failed. Measured on the bench: 1 entry
        // connected, 2, 3 and 4 each threw.
        var device = new TestableDaqifiDevice("TestDevice");
        for (var i = 0; i < staleEntries; i++)
        {
            device.StaleErrorQueue.Enqueue($"-113,\"Undefined header\"");
        }
        device.Connect();

        await device.InitializeAsync();

        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
        Assert.Empty(device.StaleErrorQueue);
    }

    [Fact]
    public async Task InitializeAsync_EmptiesTheErrorQueueBeforeTheSetupBlock()
    {
        // The order is the whole fix: the queue has to be empty before the block sends the
        // commands its terminator reports on, or the terminator can answer for something else.
        var device = new TestableDaqifiDevice("TestDevice");
        device.StaleErrorQueue.Enqueue("-113,\"Undefined header\"");
        device.StaleErrorQueue.Enqueue("-101,\"Invalid character\"");
        device.Connect();

        await device.InitializeAsync();

        var sent = device.DirectSentMessages.Select(m => m.Data).ToList();
        var firstErrorQuery = sent.FindIndex(d => d.Contains("SYSTem:ERRor?"));
        var echoOff = sent.FindIndex(d => d.Contains("SYSTem:ECHO"));

        Assert.True(firstErrorQuery >= 0, "Initialization must read the error queue.");
        Assert.True(
            firstErrorQuery < echoOff,
            "The queue must be emptied before the setup block starts.");

        // Two stale entries, then the "no error" that ends the drain, then the block's own
        // terminator: four reads in all.
        Assert.Equal(4, sent.Count(d => d.Contains("SYSTem:ERRor?")));
    }

    [Fact]
    public async Task InitializeAsync_WhenTheQueueNeverReportsNoError_StopsReadingIt()
    {
        // A device that answers the drain with something that is not an error-queue reply at
        // all — an echo of the query on a device that still has echo on, or stream data still
        // arriving — must not be asked again and again. It is not a queued entry, so the drain
        // stops and the connect carries on.
        var device = new TestableDaqifiDevice("TestDevice") { ErrorQueueReplyIsGarbage = true };
        device.Connect();

        await device.InitializeAsync();

        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(1, device.DrainErrorQueryCount);
    }

    [Fact]
    public async Task InitializeAsync_AsksForAWindowLongEnoughForTheVerdictToTrailAnEcho()
    {
        // The terminator's reply trails the commands, and on a device that echoes it is an echo
        // line that starts the inactivity clock. The other two users of this same query raise
        // their completion window to 1000ms for that reason; this path takes 500ms instead —
        // long enough for a reply that arrives ~6ms after the write on the bench, while still
        // leaving the saving #667 item 2 exists for. Pinned because it is a deliberate
        // divergence from the sibling call sites, not an oversight, and because reverting to
        // the 250ms default would silently narrow it.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        await device.InitializeAsync();

        Assert.Equal(500, device.LastCompletionTimeoutMs);
        Assert.Equal(1000, device.LastResponseTimeoutMs);
    }

    [Fact]
    public async Task InitializeAsync_WithPreserveActiveStream_DoesNotReadTheErrorQueue()
    {
        // Reading the error queue pops the entry it returns. An observe session must not take
        // one that belongs to the session actually driving the device, so it forgoes the
        // terminator — and the response-timeout saving with it (#385, #667).
        var device = new TestableDaqifiDevice("TestDevice") { PreserveActiveStream = true };
        device.Connect();

        await device.InitializeAsync();

        var sentData = device.DirectSentMessages.Select(m => m.Data).ToList();
        Assert.DoesNotContain(sentData, d => d.Contains("SYSTem:ERRor?"));
    }

    [Fact]
    public async Task InitializeAsync_WhenChannelsPopulate_ExposesPopulatedChannels()
    {
        // Arrange — device responds to GetDeviceInfo by populating channels
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert — initialization blocked until ChannelsPopulated fired, so the
        // returned device is fully populated rather than empty
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count); // 2 analog + 2 digital
    }

    [Fact]
    public async Task InitializeAsync_WhenChannelsNeverPopulate_ThrowsTimeoutExceptionAndResends()
    {
        // Arrange — device never reports channel configuration
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
        device.Connect();

        // Act & Assert — a clear timeout, not a silently-unpopulated device. ThrowsAny, because
        // the thrown type is now the ChannelPopulationTimeoutException that carries what the
        // device said when asked why (#911); it still derives from TimeoutException.
        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Equal(DeviceState.Error, device.State);
        Assert.Empty(device.Channels);
        // GetDeviceInfo is re-sent while waiting (initial request + at least one retry).
        Assert.True(device.DeviceInfoRequestCount >= 2);
    }

    [Fact]
    public async Task InitializeAsync_OnReconnect_WaitsForFreshStatusInsteadOfStaleChannels()
    {
        // Arrange — first init populates channels; the instance is then reused across a
        // reconnect (e.g. FirmwareUpdateService post-reset wake). Disconnect leaves the
        // prior session's channels in place, so initialization must wait for a fresh
        // status rather than short-circuiting on the stale channels.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();
        await device.InitializeAsync();
        Assert.NotEmpty(device.Channels); // stale channels now linger

        device.Disconnect();
        device.Connect();

        // The reconnected device does not report a fresh status.
        device.PopulateChannelsOnDeviceInfo = false;

        // Act & Assert — times out instead of returning stale channels. ThrowsAny, because the
        // thrown type is the ChannelPopulationTimeoutException that derives from it (#911).
        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(DeviceState.Error, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenCancelledDuringWait_ThrowsOperationCanceledException()
    {
        // Arrange — device never populates, so the wait loop is observing cancellation.
        // Cancellation is fired from the GetDeviceInfo request rather than from a timer:
        // that request is the wait loop's first act, so this lands in the wait loop by
        // construction. A timer would now be racing the init SCPI setup sequence, whose
        // settle delays observe the token themselves (#667) and would cancel it first.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
        device.Connect();
        using var cts = new CancellationTokenSource();
        device.OnDeviceInfoRequested = () => cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => device.InitializeAsync(TimeSpan.FromSeconds(5), cts.Token));

        // Caller-initiated cancellation is not a device fault — state must not flip to Error.
        Assert.NotEqual(DeviceState.Error, device.State);
        Assert.Equal(DeviceState.Connected, device.State);
    }

    /// <summary>
    /// The init SCPI setup sequence, in the order it is sent. Cancelling as any one of these
    /// goes out must stop the sequence there, leaving every later command unsent.
    /// </summary>
    public static TheoryData<string, string[]> InitScpiSequenceCancellationPoints => new()
    {
        { "SYSTem:ECHO", new[] { "StopStreamData", "SYSTem:POWer:STATe", "SYSTem:STReam:FORmat" } },
        { "StopStreamData", new[] { "SYSTem:POWer:STATe", "SYSTem:STReam:FORmat" } },
        { "SYSTem:POWer:STATe", new[] { "SYSTem:STReam:FORmat" } },
    };

    [Theory]
    [MemberData(nameof(InitScpiSequenceCancellationPoints))]
    public async Task InitializeAsync_WhenCancelledMidInitScpiSequence_SendsNoLaterCommand(
        string cancelAfterCommand,
        string[] commandsThatMustNotFollow)
    {
        // The point of #667 item 1: the init SCPI setup sequence used to space its commands
        // with Thread.Sleep(100), which blocked a thread-pool thread and could not observe
        // the token — a caller who cancelled during that window had to wait out the whole
        // 300ms and every remaining command still went to the device. Awaiting the delays
        // instead, and checking the token before each write, makes the sequence stop where it
        // was cancelled. The token is checked before each write and not only by the delays:
        // an awaited delay only observes cancellation while it is still pending, so
        // cancellation landing between a delay completing and the next Send would otherwise
        // still reach the device.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();
        using var cts = new CancellationTokenSource();

        // Cancel as the named command goes out, i.e. while the settle delay that follows it is
        // what the sequence is waiting on.
        device.OnCommandSent = data =>
        {
            if (data.Contains(cancelAfterCommand))
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.InitializeAsync(TimeSpan.FromSeconds(5), cts.Token));

        var sent = device.DirectSentMessages.Select(m => m.Data).ToList();
        Assert.Contains(sent, d => d.Contains(cancelAfterCommand));
        foreach (var command in commandsThatMustNotFollow)
        {
            Assert.DoesNotContain(sent, d => d.Contains(command));
        }

        // Caller-initiated cancellation is not a device fault.
        Assert.NotEqual(DeviceState.Error, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheStatusCannotIdentifyTheDevice_FailsInsteadOfReportingAWrongDevice()
    {
        // The bench state behind issue #922. The device answers with 16 analog channels, no
        // digital channels and no identity at all. Everything the connect derives from that is
        // wrong: half the channels are missing, the analog channels keep their assumed 65535
        // counts instead of the board's real 4096, and the rate ceiling keeps the Unknown-board
        // placeholder of 1000 Hz instead of 22000. Nothing downstream can tell, because the
        // device reports Connected and has channels. So the connect has to fail.
        var device = new TestableDaqifiDevice("TestDevice")
        {
            StatusForDeviceInfo = _ => new DaqifiOutMessage { AnalogInPortNum = 16 },
            IdentityReply = "DAQiFi,Nq1,7E2815916200E898,01-02"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(300)));

        // The message names what was missing, so a caller is not left guessing at a device
        // that answered.
        Assert.Contains("never identified itself", ex.Message);
        Assert.Contains("no part number and no firmware version", ex.Message);
        Assert.Contains("16 analog and 0 digital", ex.Message);

        // And it says the device knows what it is, which is what makes this a device to reboot
        // rather than a connection to check. A device that just sent a status is responding.
        Assert.True(ex.IsDeviceResponding);
        Assert.Equal("DAQiFi,Nq1,7E2815916200E898,01-02", ex.DeviceIdentity);
        Assert.Contains("DAQiFi,Nq1,7E2815916200E898,01-02", ex.Message);

        Assert.Equal(DeviceState.Error, device.State);
        Assert.Empty(device.Metadata.PartNumber);
        Assert.Equal(DeviceType.Unknown, device.Metadata.DeviceType);
    }

    [Theory]
    [InlineData("", "3.8.0", "no part number")]
    [InlineData("Nq1", "", "no firmware version")]
    public async Task InitializeAsync_WhenTheStatusCarriesHalfAnIdentity_SaysWhichHalfIsMissing(
        string partNumber, string firmwareVersion, string expectedMissing)
    {
        // Half an identity is not an identity. The part number selects the board, and with it
        // the channel resolution and the rate ceiling; the firmware version decides whether the
        // capability document that would replace those assumptions is read at all.
        var device = new TestableDaqifiDevice("TestDevice")
        {
            StatusForDeviceInfo = _ => new DaqifiOutMessage
            {
                DevicePn = partNumber,
                DeviceFwRev = firmwareVersion,
                AnalogInPortNum = 16,
                DigitalPortNum = 16
            }
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(300)));

        Assert.Contains(expectedMissing, ex.Message);
        Assert.Equal(DeviceState.Error, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenAStatusWithoutIdentityIsFollowedByAGoodOne_Succeeds()
    {
        // A status that cannot identify the device is a frame to discard, not a verdict on the
        // connect. The request is re-sent, and a device that identifies itself on a later status
        // is a good connect.
        var device = new TestableDaqifiDevice("TestDevice")
        {
            StatusForDeviceInfo = request => request == 1
                ? new DaqifiOutMessage { AnalogInPortNum = 16 }
                : HealthyStatus()
        };
        device.Connect();

        await device.InitializeAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
        Assert.Equal("Nq1", device.Metadata.PartNumber);
        Assert.Equal("3.8.0", device.Metadata.FirmwareVersion);
        Assert.True(device.DeviceInfoRequestCount > 1, "The discarded status must be re-requested.");
    }

    [Fact]
    public async Task InitializeAsync_WhenNoStatusArrivesAtAll_DoesNotBlameTheIdentity()
    {
        // The two failures are different, and a caller acts on them differently: a device that
        // sent nothing is a connection or routing problem, while a device that sent a status
        // Core could not use is this one. The messages must not be interchangeable.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(300)));

        Assert.DoesNotContain("never identified itself", ex.Message);
    }

    [Fact]
    public async Task InitializeAsync_WhenChannelsPopulateAsynchronously_CompletesViaWaitLoop()
    {
        // Arrange — channels arrive on a background thread after a delay (as on real hardware
        // via the consumer thread), forcing the Task.WhenAny wait loop rather than the
        // synchronous short-circuit.
        var device = new TestableDaqifiDevice("TestDevice")
        {
            AsyncPopulationDelay = TimeSpan.FromMilliseconds(150)
        };
        device.Connect();

        // Act
        await device.InitializeAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
    }

    [Fact]
    public async Task InitializeAsync_StreamingUsb_Succeeds_SetsReadyAndRoutesToUsb()
    {
        // Arrange
        var device = new TestableStreamingDevice("TestDevice");
        device.Connect();

        // Act
        await device.InitializeAsync();

        // Assert — device is ready and the USB stream-interface command was sent
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
        Assert.Contains(device.SentData, d => d.Contains("SYSTem:STReam:INTerface 0"));
    }

    [Fact]
    public async Task InitializeAsync_StreamingUsb_RoutesTheStreamToUsbBeforeAskingForStatus()
    {
        // Issue #897. The firmware keeps the stream interface across sessions, so a device a
        // WiFi session left on SYSTem:STReam:INTerface 1 sends its status reply over WiFi. The
        // routing command therefore has to reach the device before SYSInfoPB?, or the connect
        // waits 8s for a reply that went somewhere else.
        var device = new TestableStreamingDevice("TestDevice");
        device.Connect();

        await device.InitializeAsync();

        var sent = device.SentData.ToList();
        var routeIndex = sent.FindIndex(d => d.Contains("SYSTem:STReam:INTerface 0"));
        var statusIndex = sent.FindIndex(d => d.Contains("SYSInfoPB"));

        Assert.True(routeIndex >= 0, "Initialization must route the stream to USB.");
        Assert.True(
            routeIndex < statusIndex,
            "The stream must be routed to USB before the device is asked for its status.");
    }

    [Fact]
    public async Task InitializeAsync_StreamingUsb_RoutesTheStreamInsideTheSetupBlock()
    {
        // The routing command belongs with the other commands that put the device into a known
        // state, which means before the setup block's SYSTem:ERRor? terminator. That placement
        // is what gives it the block's own error check, and what let its separate exchange,
        // retry loop and exception path be deleted (#897).
        var device = new TestableStreamingDevice("TestDevice");
        device.Connect();

        await device.InitializeAsync();

        var sent = device.SentData.ToList();
        var routeIndex = sent.FindIndex(d => d.Contains("SYSTem:STReam:INTerface 0"));
        // The last one, so the test still means this if initialization ever reads the error
        // queue before the block as well.
        var terminatorIndex = sent.FindLastIndex(d => d.Contains("SYSTem:ERRor?"));

        Assert.True(routeIndex >= 0, "Initialization must route the stream to USB.");
        Assert.True(terminatorIndex >= 0, "The setup block must query the error queue.");
        Assert.True(
            routeIndex < terminatorIndex,
            "The routing command must precede the terminator that reports on the setup block.");
    }

    [Fact]
    public async Task InitializeAsync_StreamingUsb_WhenAPriorWifiSessionLeftTheStreamOnWifi_StillConnects()
    {
        // Issue #897, reproduced: this device answers SYSInfoPB? only once its stream interface
        // points at USB, which is what the firmware does. Before the fix the connect asked for
        // status first, so the wait timed out and the step that would have repaired the routing
        // never ran — 0 of 3 USB connects succeeded on the bench.
        var device = new TestableStreamingDevice("TestDevice") { StreamInterfaceIsWifi = true };
        device.Connect();

        await device.InitializeAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(4, device.Channels.Count);
    }

    [Fact]
    public async Task InitializeAsync_StreamingUsb_WhenRoutingIsRejected_SetsErrorNotReady()
    {
        // The routing command is now judged by the setup block's own error check, so a device
        // that rejects it still fails the initialization — with the same typed exception, and
        // without the separate retry the command used to carry.
        var device = new TestableStreamingDevice("TestDevice") { RejectStreamInterfaceCommand = true };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(
            () => device.InitializeAsync());

        Assert.Equal(DeviceState.Error, device.State);
        Assert.NotNull(ex.LastScpiError);
    }

    [Fact]
    public async Task InitializeAsync_WhenChannelsNeverPopulateButTheDeviceAnswers_SaysSoAndNamesTheInterface()
    {
        // Issue #911. The old message named two causes, unresponsive or still starting, and the
        // one seen most often on the bench was neither: the device answered every text query at
        // once and was sending its output to another interface. Reading that message cost a
        // bench session a dead-device diagnosis, a bootloader check and a power cycle.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            IdentityReply = "DAQiFi,Nq1,7E2815916200E898,01-02",
            StreamInterfaceReply = "1"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Contains("did not report its channel configuration", ex.Message);
        Assert.Contains("The device is responding", ex.Message);
        Assert.Contains("DAQiFi,Nq1,7E2815916200E898,01-02", ex.Message);
        Assert.Contains("stream interface 1", ex.Message);
        Assert.DoesNotContain("may be unresponsive", ex.Message);

        // As data, so an application can act on it rather than parse English.
        Assert.True(ex.IsDeviceResponding);
        Assert.Equal("DAQiFi,Nq1,7E2815916200E898,01-02", ex.DeviceIdentity);
        Assert.Equal(1, ex.StreamInterface);
        Assert.Contains("DAQiFi,Nq1,7E2815916200E898,01-02", ex.RawDeviceResponse);
    }

    [Fact]
    public async Task InitializeAsync_WhenChannelsNeverPopulateAndTheDeviceIsSilent_KeepsTheOldMessage()
    {
        // A device that will not answer *IDN? either really may be absent or starting, so the
        // original wording is the accurate one and is kept. It is also not asked the second
        // question: it has already said what it had to say.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false);
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Contains("The device may be unresponsive or still initializing.", ex.Message);
        Assert.DoesNotContain("is responding", ex.Message);
        Assert.False(ex.IsDeviceResponding);
        Assert.Null(ex.DeviceIdentity);
        Assert.Null(ex.StreamInterface);
        Assert.Equal(0, ex.UnreadableResponseLines);
        // Asked once. A device that returns nothing has already said what it had to say.
        Assert.Equal(1, device.DiagnosticQueryCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDeviceEchoesTheDiagnosticQuery_DoesNotReadTheEchoAsTheAnswer()
    {
        // A device reset mid-session has echo on again, so the first line back is the query
        // itself. Reading it as the answer would report the device as responding with "*IDN?".
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            EchoesQueries = true,
            IdentityReply = "DAQiFi,Nq1,7E2815916200E898,01-02",
            StreamInterfaceReply = "0"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Equal("DAQiFi,Nq1,7E2815916200E898,01-02", ex.DeviceIdentity);
        Assert.Equal(0, ex.StreamInterface);
    }

    [Fact]
    public async Task InitializeAsync_WhenAProtobufFrameArrivesWithTheAnswer_DoesNotReportItAsTheIdentity()
    {
        // Measured on the bench. The diagnostic exchange pauses the protobuf consumer, so bytes
        // of a status frame already in flight are read as lines of the reply. One landed after
        // the *IDN? answer and was reported as the device's identity, which made the new message
        // less readable than the old one.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            IdentityReply = "DAQiFi,Nq1,7E2815916200E898,01-02",
            TrailingProtobufNoise = "\u0001\u0012\u0000DAQiFi-95AC\u0000\u00ffNq1\u0000",
            StreamInterfaceReply = "1"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Equal("DAQiFi,Nq1,7E2815916200E898,01-02", ex.DeviceIdentity);
        Assert.Equal(1, ex.StreamInterface);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDeviceFloodsUnreadableData_SaysItIsSendingNotSilent()
    {
        // Measured on the bench: 4 of 5 forced timeouts had the device send about 2500 lines of
        // protobuf and answer no text query at all. That device is not unresponsive, and saying
        // it is sends the reader looking for a dead board instead of at the stream format.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            TrailingProtobufNoise = "\u0001\u0012\u0000\u00ffNq1\u0000",
            StreamInterfaceReply = "1"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Contains("The device is sending data", ex.Message);
        Assert.Contains("The device is not silent", ex.Message);
        Assert.DoesNotContain("may be unresponsive", ex.Message);
        Assert.Equal(1, ex.UnreadableResponseLines);
        Assert.False(ex.IsDeviceResponding);

        // Worth asking the second question of a device that is plainly alive.
        Assert.Equal(1, ex.StreamInterface);
        Assert.Equal(2, device.DiagnosticQueryCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDeviceFloodsUnreadableData_DoesNotCarryItAllOnTheException()
    {
        // An exception is not the place for 2500 lines of binary.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            IdentityReply = "DAQiFi,Nq1,7E2815916200E898,01-02",
            NoiseLineCount = 2500,
            TrailingProtobufNoise = "\u0001\u0012\u0000\u00ffNq1\u0000"
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Equal("DAQiFi,Nq1,7E2815916200E898,01-02", ex.DeviceIdentity);
        Assert.True(ex.RawDeviceResponse.Count <= 20, $"kept {ex.RawDeviceResponse.Count} lines");
        Assert.All(ex.RawDeviceResponse, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDiagnosticQuestionsThemselvesFail_StillReportsTheTimeout()
    {
        // The questions run while an exception is being built. A failure to diagnose must not
        // replace the failure being reported.
        var device = new TestableDaqifiDevice("TestDevice", populateChannelsOnDeviceInfo: false)
        {
            DiagnosticQueriesThrow = true
        };
        device.Connect();

        var ex = await Assert.ThrowsAsync<ChannelPopulationTimeoutException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(150)));

        Assert.Contains("The device may be unresponsive or still initializing.", ex.Message);
        Assert.False(ex.IsDeviceResponding);
        Assert.Equal(DeviceState.Error, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenItSucceeds_AsksNoDiagnosticQuestions()
    {
        // The questions are on the failure path only, so a connect that works pays nothing.
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        await device.InitializeAsync();

        Assert.Equal(0, device.DiagnosticQueryCount);
        Assert.DoesNotContain(device.DirectSentMessages.Select(m => m.Data), d => d.Contains("*IDN?"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task InitializeAsync_WithNonPositiveTimeout_ThrowsArgumentOutOfRangeException(int timeoutMs)
    {
        // Arrange
        var device = new TestableDaqifiDevice("TestDevice");
        device.Connect();

        // Act & Assert — a misconfigured timeout is an argument error, not a device timeout
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.InitializeAsync(TimeSpan.FromMilliseconds(timeoutMs)));

        // Misconfiguration must not flip the device into an error state.
        Assert.Equal(DeviceState.Connected, device.State);
    }

    /// <summary>
    /// A testable DaqifiDevice that captures sent messages without needing a real transport.
    /// Overrides ExecuteTextCommandAsync to bypass transport requirements in unit tests.
    /// </summary>
    internal class TestableDaqifiDevice : DaqifiDevice
    {
        private readonly IReadOnlyList<string> _textCommandResponse;

        /// <summary>
        /// All messages sent via direct Send() calls.
        /// </summary>
        public List<IOutboundMessage<string>> DirectSentMessages { get; } = new();

        /// <summary>
        /// Number of GetDeviceInfo (SYSInfoPB?) requests observed.
        /// </summary>
        public int DeviceInfoRequestCount { get; private set; }

        /// <summary>
        /// Number of times the init SCPI setup sequence's ExecuteTextCommandAsync was invoked.
        /// </summary>
        public int TextCommandAttemptCount { get; private set; }

        /// <summary>
        /// Entries the device's error queue already held before this session, in the order
        /// <c>SYSTem:ERRor?</c> pops them. The real device keeps them across a disconnect,
        /// which is what made every connect fail before #898.
        /// </summary>
        public Queue<string> StaleErrorQueue { get; } = new();

        /// <summary>
        /// Number of <c>SYSTem:ERRor?</c> queries the drain sent, i.e. not counting the setup
        /// block's own terminator.
        /// </summary>
        public int DrainErrorQueryCount { get; private set; }

        /// <summary>
        /// When true, the error queue answers with a line that is not an error-queue reply at
        /// all — what a device with echo still on, or one still sending stream data, looks like.
        /// </summary>
        public bool ErrorQueueReplyIsGarbage { get; set; }

        /// <summary>
        /// The response and completion timeouts the last text exchange asked for. The init
        /// sequence's completion window is what its saving comes out of (#667), so it is a
        /// deliberate value rather than an incidental one, and a test can check it.
        /// </summary>
        public int LastResponseTimeoutMs { get; private set; }

        /// <inheritdoc cref="LastResponseTimeoutMs"/>
        public int LastCompletionTimeoutMs { get; private set; }

        /// <summary>
        /// When true, a GetDeviceInfo request synchronously populates channels (simulating
        /// the device's status response). Settable so tests can toggle the behavior between
        /// initialization attempts.
        /// </summary>
        public bool PopulateChannelsOnDeviceInfo { get; set; }

        /// <summary>
        /// The status message to answer the n-th GetDeviceInfo request with, where n starts at 1.
        /// <c>null</c>, the default, answers every request with <see cref="HealthyStatus"/>. A
        /// test uses this to answer with a status a real device has been seen to send and a
        /// connect must not accept — one that reports channels and does not say which device
        /// sent it (issue #922).
        /// </summary>
        public Func<int, DaqifiOutMessage>? StatusForDeviceInfo { get; set; }

        /// <summary>
        /// The capability-document read is a text exchange of its own, and a connect makes it on
        /// every device that reports a firmware version — which a real status message always
        /// does, and which a connect now insists on (issue #922). Answered here without an
        /// exchange so the counters above keep measuring the init setup sequence, which is what
        /// the tests reading them are about. The read itself is covered by its own tests.
        /// </summary>
        public override Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<CapabilityDocument?>(null);

        /// <summary>
        /// When set, channels populate asynchronously after this delay on a background thread
        /// (simulating a status that arrives via the consumer thread) instead of synchronously
        /// inside Send. This exercises the production Task.WhenAny wait loop rather than the
        /// synchronous short-circuit.
        /// </summary>
        public TimeSpan? AsyncPopulationDelay { get; set; }

        /// <summary>
        /// Invoked when a GetDeviceInfo (SYSInfoPB?) request is observed. That request is the
        /// first thing <c>WaitForChannelsPopulatedAsync</c> sends, so a test that needs
        /// cancellation to land inside the wait loop — rather than earlier, in the init SCPI
        /// setup sequence, whose settle delays are now cancellable (#667) — can cancel here
        /// and get that placement deterministically instead of racing a timer against it.
        /// </summary>
        public Action? OnDeviceInfoRequested { get; set; }

        /// <summary>
        /// Invoked with the text of every command the device sends, so a test can act at a
        /// precise point in a command sequence rather than by waiting a guessed interval.
        /// </summary>
        public Action<string>? OnCommandSent { get; set; }

        /// <summary>
        /// The device's answer to <c>*IDN?</c>, or <c>null</c> for a device that does not
        /// answer it at all.
        /// </summary>
        public string? IdentityReply { get; set; }

        /// <summary>
        /// The device's answer to <c>SYSTem:STReam:INTerface?</c>, or <c>null</c> for no answer.
        /// </summary>
        public string? StreamInterfaceReply { get; set; }

        /// <summary>
        /// When true, the device echoes a query back before answering it, the way one that has
        /// not had <c>SYSTem:ECHO -1</c> applied does.
        /// </summary>
        public bool EchoesQueries { get; set; }

        /// <summary>
        /// A line of a protobuf status frame that arrives after the answer, the way bytes
        /// already in flight do when the exchange pauses the protobuf consumer.
        /// </summary>
        public string? TrailingProtobufNoise { get; set; }

        /// <summary>
        /// How many times <see cref="TrailingProtobufNoise"/> is repeated, for a device that is
        /// flooding the connection rather than dribbling into it.
        /// </summary>
        public int NoiseLineCount { get; set; } = 1;

        /// <summary>
        /// When true, the two questions asked after a channel-population timeout throw instead
        /// of answering.
        /// </summary>
        public bool DiagnosticQueriesThrow { get; set; }

        /// <summary>
        /// Number of diagnostic questions (<c>*IDN?</c>, <c>SYSTem:STReam:INTerface?</c>) the
        /// device was asked. A connect that works must be charged none of them.
        /// </summary>
        public int DiagnosticQueryCount { get; private set; }

        public TestableDaqifiDevice(
            string name,
            IPAddress? ipAddress = null,
            IReadOnlyList<string>? textCommandResponse = null,
            bool populateChannelsOnDeviceInfo = true)
            : base(name, ipAddress)
        {
            _textCommandResponse = textCommandResponse ?? Array.Empty<string>();
            PopulateChannelsOnDeviceInfo = populateChannelsOnDeviceInfo;
        }

        /// <summary>
        /// Builds the double over a transport, so the real connect, disconnect and lost-connection
        /// paths run and the real message pumps are built. The text exchange stays bypassed, so
        /// <see cref="DaqifiDevice.InitializeAsync"/> still reaches
        /// <see cref="DeviceState.Ready"/>.
        /// </summary>
        public TestableDaqifiDevice(string name, Daqifi.Core.Communication.Transport.IStreamTransport transport)
            : base(name, transport)
        {
            _textCommandResponse = Array.Empty<string>();
            PopulateChannelsOnDeviceInfo = true;
        }

        /// <summary>
        /// When true, <see cref="Send{T}"/> also takes the production path, so a test can see
        /// which stream the real message producer writes to.
        /// </summary>
        public bool SendThroughProducer { get; set; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (SendThroughProducer)
            {
                base.Send(message);
            }

            if (message is IOutboundMessage<string> stringMessage)
            {
                DirectSentMessages.Add(stringMessage);
                OnCommandSent?.Invoke(stringMessage.Data);

                // Simulate the device responding to GetDeviceInfo (SYSInfoPB?) with a
                // protobuf status message that populates channels. The real flow does this
                // via the protobuf consumer, which has no backing transport in unit tests.
                if (stringMessage.Data.Contains("SYSInfoPB"))
                {
                    DeviceInfoRequestCount++;
                    OnDeviceInfoRequested?.Invoke();
                    if (PopulateChannelsOnDeviceInfo)
                    {
                        var requestNumber = DeviceInfoRequestCount;
                        var status = StatusForDeviceInfo?.Invoke(requestNumber) ?? HealthyStatus();

                        if (AsyncPopulationDelay is { } delay)
                        {
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(delay);
                                OnStatusMessageReceived(status);
                            });
                        }
                        else
                        {
                            OnStatusMessageReceived(status);
                        }
                    }
                }
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
            LastResponseTimeoutMs = responseTimeoutMs;
            LastCompletionTimeoutMs = completionTimeoutMs;

            var before = DirectSentMessages.Count;
            var lines = await RunTextExchangeAsync(
                _ => { setupAction(); return Task.CompletedTask; },
                cancellationToken,
                prepareAsync,
                finalizeAsync).ConfigureAwait(false);

            // The two questions asked only after a channel-population timeout (#911) come
            // through this overload, one query per exchange. Everything else is passed on
            // untouched.
            var sent = DirectSentMessages.Skip(before).Select(m => m.Data).ToList();
            var identityQuery = sent.FirstOrDefault(d => d.Contains("*IDN?"));
            var interfaceQuery = sent.FirstOrDefault(d => d.Contains("STReam:INTerface?"));
            if (identityQuery == null && interfaceQuery == null)
            {
                return lines;
            }

            DiagnosticQueryCount++;
            if (DiagnosticQueriesThrow)
            {
                throw new InvalidOperationException("no transport");
            }

            var query = identityQuery ?? interfaceQuery!;
            var answer = identityQuery != null ? IdentityReply : StreamInterfaceReply;
            var replyLines = new List<string>();
            if (EchoesQueries)
            {
                replyLines.Add(query);
            }

            if (answer != null)
            {
                replyLines.Add(answer);
            }

            if (TrailingProtobufNoise != null)
            {
                for (var i = 0; i < NoiseLineCount; i++)
                {
                    replyLines.Add(TrailingProtobufNoise);
                }
            }

            return replyLines;
        }

        // The init SCPI setup sequence uses the async overload so its settle delays do not
        // block a thread-pool thread (#667). Both overloads funnel into the same body here,
        // mirroring the production class, so the double keeps intercepting either one.
        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            LastResponseTimeoutMs = responseTimeoutMs;
            LastCompletionTimeoutMs = completionTimeoutMs;
            return RunTextExchangeAsync(setupActionAsync, cancellationToken, prepareAsync: null, finalizeAsync: null);
        }

        private async Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Func<CancellationToken, Task> setupActionAsync,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync)
        {
            try
            {
                // Honor the exchange's prepare phase the way the real device does: it runs first,
                // before anything this exchange sends (#396).
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                // Run the setup action so that Send() calls inside it are captured
                var before = DirectSentMessages.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                var sentThisCall = DirectSentMessages.Skip(before).Select(m => m.Data).ToList();

                // Echo-off is the first thing the setup block sends, and nothing else sends it,
                // so it tells the two kinds of exchange apart: the block itself, and the
                // error-queue drain that now runs before it (#898).
                if (sentThisCall.Any(d => d.Contains("SYSTem:ECHO")))
                {
                    TextCommandAttemptCount++;
                    if (_textCommandResponse.Count > 0)
                    {
                        return _textCommandResponse;
                    }
                }
                else if (sentThisCall.Any(d => d.Contains("SYSTem:ERRor?")))
                {
                    DrainErrorQueryCount++;
                    if (ErrorQueueReplyIsGarbage)
                    {
                        return new[] { "SYSTem:ERRor?\r\n" };
                    }
                }

                // Answer any SYSTem:ERRor? the way the device does: pop the oldest entry, or
                // report an empty queue.
                if (sentThisCall.Any(d => d.Contains("SYSTem:ERRor?")))
                {
                    return new[]
                    {
                        StaleErrorQueue.Count > 0
                            ? StaleErrorQueue.Dequeue()
                            : "0,\"No error\""
                    };
                }

                return _textCommandResponse;
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
    }

    [Theory]
    [InlineData(true)]  // observing: the hook returns immediately, no awaitable work
    [InlineData(false)] // take-control: the hook does send SCPI
    public async Task InitializeAsync_WhenCancelledAfterChannelsPopulate_DoesNotReportReady(bool preserveActiveStream)
    {
        // Arrange — cancellation lands at the one seam nothing was guaranteed to observe:
        // after channels populate, during the capability read. Firmware that does not
        // advertise the capability document returns from that read without touching the
        // token, and on the observing path the derived hook then returns immediately too, so
        // a device whose caller had already cancelled still reached Ready.
        using var cts = new CancellationTokenSource();
        var device = new CancelDuringCapabilityReadDevice("TestDevice", cts)
        {
            PreserveActiveStream = preserveActiveStream
        };
        device.Connect();

        // Act & Assert — a cancelled initialization reports cancellation, not success.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => device.InitializeAsync(TimeSpan.FromSeconds(5), cts.Token));

        Assert.NotEqual(DeviceState.Ready, device.State);
        Assert.Equal(DeviceState.Connected, device.State);
    }

    /// <summary>
    /// A testable USB streaming device that cancels the supplied token from inside the
    /// capability read — i.e. after channels have populated and before the derived
    /// initialization hook — and returns without observing the token itself, exactly as the
    /// real read does on firmware that does not advertise a capability document.
    /// </summary>
    private class CancelDuringCapabilityReadDevice : DaqifiStreamingDevice
    {
        private readonly CancellationTokenSource _cts;

        public override bool IsUsbConnection => true;

        public CancelDuringCapabilityReadDevice(string name, CancellationTokenSource cts)
            : base(name, (IPAddress?)null)
        {
            _cts = cts;
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage &&
                stringMessage.Data.Contains("SYSInfoPB"))
            {
                OnStatusMessageReceived(HealthyStatus());
            }
        }

        public override Task<Daqifi.Core.Device.Capabilities.CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
        {
            _cts.Cancel();
            return Task.FromResult<Daqifi.Core.Device.Capabilities.CapabilityDocument?>(null);
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            return RunTextExchangeAsync(
                _ => { setupAction(); return Task.CompletedTask; },
                cancellationToken,
                prepareAsync,
                finalizeAsync);
        }

        // The init SCPI setup sequence uses the async overload (#667); intercept both.
        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return RunTextExchangeAsync(setupActionAsync, cancellationToken, prepareAsync: null, finalizeAsync: null);
        }

        private async Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Func<CancellationToken, Task> setupActionAsync,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync)
        {
            try
            {
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                return Array.Empty<string>();
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
    }

    [Fact]
    public async Task InitializeAsync_WhenASecondCallOverlapsTheFirst_TheFirstCallsDecisionStands()
    {
        // Arrange — the race reported against the first cut of #385: the observing decision
        // was held in an instance field, so a second InitializeAsync starting while the first
        // was still in flight overwrote it, and the first initialization's USB routing step
        // then acted on the second one's decision. Reproduced by holding an observing
        // initialization inside its first SCPI exchange — past the point the decision is made,
        // before the routing step — while a take-control call starts on the same instance.
        var device = new OverlappingInitDevice("TestDevice");
        device.Connect();

        device.PreserveActiveStream = true;
        var observing = device.InitializeAsync();

        // Wait until the observing call is parked inside its first exchange.
        await device.FirstExchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        device.PreserveActiveStream = false;
        var overlapping = device.InitializeAsync();

        // Act — let the observing initialization finish, now that the flag has moved under it.
        device.ReleaseFirstExchange();
        await observing.WaitAsync(TimeSpan.FromSeconds(5));
        await overlapping.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — the one initialization that ran reached its hook with the decision it
        // started with. The overlapping call waited for it rather than running a second
        // sequence over the top of it (issue #943), so there is no second decision at all.
        // A shared field yields {false}.
        Assert.Equal(new[] { true }, device.HookDecisions.ToArray());
        Assert.Equal(DeviceState.Ready, device.State);
    }

    /// <summary>
    /// A testable USB streaming device that records the decision each initialization passes to
    /// <c>OnDeviceInitializingAsync</c>, and can park its first text exchange so a second
    /// initialization can be started while the first is still in flight.
    /// </summary>
    private class OverlappingInitDevice : DaqifiStreamingDevice
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exchangeCount;

        /// <summary>Completes once an initialization has entered its first text exchange.</summary>
        public TaskCompletionSource FirstExchangeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The decision each initialization handed to the derived hook.</summary>
        public System.Collections.Concurrent.ConcurrentBag<bool> HookDecisions { get; } = new();

        public override bool IsUsbConnection => true;

        public OverlappingInitDevice(string name) : base(name, (IPAddress?)null) { }

        public void ReleaseFirstExchange() => _release.TrySetResult();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage &&
                stringMessage.Data.Contains("SYSInfoPB"))
            {
                OnStatusMessageReceived(HealthyStatus());
            }
        }

        protected override Task OnDeviceInitializingAsync(
            bool preserveActiveStream,
            CancellationToken cancellationToken)
        {
            HookDecisions.Add(preserveActiveStream);
            return Task.CompletedTask;
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            return RunTextExchangeAsync(
                _ => { setupAction(); return Task.CompletedTask; },
                cancellationToken,
                prepareAsync,
                finalizeAsync);
        }

        // The init SCPI setup sequence uses the async overload (#667); intercept both. This
        // double's whole purpose is to park the first exchange, so missing one of the two
        // overloads would silently stop it parking anything.
        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return RunTextExchangeAsync(setupActionAsync, cancellationToken, prepareAsync: null, finalizeAsync: null);
        }

        private async Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Func<CancellationToken, Task> setupActionAsync,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync)
        {
            try
            {
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                await setupActionAsync(cancellationToken).ConfigureAwait(false);

                // Park only the very first exchange — the one belonging to the initialization
                // the test starts first.
                if (Interlocked.Increment(ref _exchangeCount) == 1)
                {
                    FirstExchangeEntered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                }

                return Array.Empty<string>();
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
    }

    /// <summary>
    /// A testable DaqifiStreamingDevice (always USB) whose base init populates channels on
    /// GetDeviceInfo, and which can model the firmware's persisted stream interface and a
    /// rejection of the routing command.
    /// </summary>
    private class TestableStreamingDevice : DaqifiStreamingDevice
    {
        private readonly List<string> _sent = new();

        public IReadOnlyList<string> SentData => _sent;

        /// <summary>
        /// When true, the device starts out routing its stream to WiFi, the way the firmware
        /// does after a WiFi session, and answers SYSInfoPB? only once it has been routed back
        /// to USB (#897).
        /// </summary>
        public bool StreamInterfaceIsWifi { get; set; }

        /// <summary>
        /// When true, the device answers the routing command with a SCPI error.
        /// </summary>
        public bool RejectStreamInterfaceCommand { get; set; }

        /// <summary>
        /// Invoked once, from inside the first text exchange of initialization — i.e. after
        /// InitializeAsync has captured its PreserveActiveStream decision but before the
        /// derived USB step runs. Lets a test mutate device state mid-initialization.
        /// </summary>
        public Action? MutateDuringInitialization { get; set; }

        private bool _mutationApplied;

        public override bool IsUsbConnection => true;

        public TestableStreamingDevice(string name)
            : base(name, (IPAddress?)null)
        {
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is not IOutboundMessage<string> stringMessage)
            {
                return;
            }

            _sent.Add(stringMessage.Data);

            if (stringMessage.Data.Contains("SYSTem:STReam:INTerface 0"))
            {
                StreamInterfaceIsWifi = false;
            }

            // The status reply goes to whichever interface the firmware is routing to, so a
            // device still set to WiFi sends nothing back over USB. That is the whole of #897.
            if (stringMessage.Data.Contains("SYSInfoPB") && !StreamInterfaceIsWifi)
            {
                // Through OnStatusMessageReceived, not PopulateChannelsFromStatus: the status
                // has to pass the identity check #923 added, so this double answers the way a
                // real device does rather than around the gate the connect now applies.
                OnStatusMessageReceived(HealthyStatus());
            }
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            return RunTextExchangeAsync(
                _ => { setupAction(); return Task.CompletedTask; },
                cancellationToken,
                prepareAsync,
                finalizeAsync);
        }

        // The init SCPI setup sequence uses the async overload (#667); intercept both, so the
        // MutateDuringInitialization hook and the USB-step classification below still run.
        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return RunTextExchangeAsync(setupActionAsync, cancellationToken, prepareAsync: null, finalizeAsync: null);
        }

        private async Task<IReadOnlyList<string>> RunTextExchangeAsync(
            Func<CancellationToken, Task> setupActionAsync,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync)
        {
            try
            {
                // Honor the exchange's prepare phase the way the real device does: it runs first,
                // before anything this exchange sends (#396).
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!_mutationApplied && MutateDuringInitialization != null)
                {
                    _mutationApplied = true;
                    MutateDuringInitialization();
                }

                var before = _sent.Count;
                await setupActionAsync(cancellationToken).ConfigureAwait(false);
                var sentThisCall = _sent.Skip(before).ToList();

                if (RejectStreamInterfaceCommand &&
                    sentThisCall.Any(d => d.Contains("STReam:INTerface")))
                {
                    return new[] { "**ERROR: -200, \"Execution error\"\r\n" };
                }

                return Array.Empty<string>();
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
    }
}
