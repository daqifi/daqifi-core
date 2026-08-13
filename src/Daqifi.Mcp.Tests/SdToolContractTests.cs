using Daqifi.Core.Device.SdCard;
using Daqifi.Mcp.Tools;
using ModelContextProtocol;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the SD-card tools with a card actually attached (#465) — the half
/// <see cref="SdCardAgentGuardTests"/> cannot reach, since those run with nothing connected.
/// </summary>
public class SdCardToolContractTests
{
    [Fact]
    public async Task ListSdFiles_OnAnEmptyCard_ReturnsAnEmptyListRatherThanFailing()
    {
        // Documented contract: an empty list always means an empty card. A device that could not
        // answer raises instead, so an agent can trust the difference.
        var (agent, _) = AgentHarness.WithConnectedDevice();

        var listing = await agent.ListSdFilesAsync(AgentHarness.DeviceId, CancellationToken.None);

        Assert.Equal(0, listing.FileCount);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task ListSdFiles_ReportsWhatTheCardStatesAndNullsWhatItDoesNot()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        var created = new DateTime(2026, 8, 13, 9, 30, 0, DateTimeKind.Utc);
        device.SdFiles.Add(new SdCardFileInfo("log_a.bin", created, sizeInBytes: 0));
        device.SdFiles.Add(new SdCardFileInfo("log_b.bin"));

        var listing = await agent.ListSdFilesAsync(AgentHarness.DeviceId, CancellationToken.None);

        Assert.Equal(2, listing.FileCount);

        // A size of 0 is a real (empty) file; only an unknown size is null. Collapsing the two
        // would have an agent skip a file that exists.
        var a = listing.Files.Single(f => f.FileName == "log_a.bin");
        Assert.Equal(0, a.SizeBytes);
        Assert.Equal(created, a.CreatedDate);

        var b = listing.Files.Single(f => f.FileName == "log_b.bin");
        Assert.Null(b.SizeBytes);
        Assert.Null(b.CreatedDate);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("storage")]
    public async Task ABusyCard_IsReportedAsSomethingTheAgentCanActOn(string tool)
    {
        // Core's message says the card is busy; only this layer knows the next step is a tool call.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.SdFailure = new SdCardBusyException(new[] { "busy" });

        Task Call() => tool == "list"
            ? agent.ListSdFilesAsync(AgentHarness.DeviceId, CancellationToken.None)
            : agent.GetSdStorageAsync(AgentHarness.DeviceId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);

        Assert.Contains("stop_sd_logging", ex.Message);
        // The original is kept, so the raw device response is still available for diagnosis.
        Assert.IsType<SdCardBusyException>(ex.InnerException);
    }

    [Fact]
    public async Task AnEmptyTransfer_ExplainsTheStreamingInteractionThatCausesIt()
    {
        // A live stream collapses the device's SD buffer, and the symptom (an empty transfer)
        // gives no hint of the cause — so the tool has to supply it.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.SdFailure = new SdCardEmptyTransferException("log_a.bin", listedSizeInBytes: 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ListSdFilesAsync(AgentHarness.DeviceId, CancellationToken.None));

        Assert.Contains("streaming", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SdCardEmptyTransferException>(ex.InnerException);
    }

    [Fact]
    public async Task AnUnrecognizedCardFailure_IsLeftExactlyAsCoreRaisedIt()
    {
        // Only the two failures with an MCP-specific next step are rewritten; rewriting the rest
        // would replace messages that are already written for a human.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.SdFailure = new TimeoutException("the device stopped answering");

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => agent.ListSdFilesAsync(AgentHarness.DeviceId, CancellationToken.None));

        Assert.Equal("the device stopped answering", ex.Message);
    }

    [Fact]
    public async Task GetSdStorage_ReportsUsedSpaceAndPercentFree()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.SdStorage = new SdCardStorageInfo(FreeBytes: 333, TotalBytes: 1000);

        var report = await agent.GetSdStorageAsync(AgentHarness.DeviceId, CancellationToken.None);

        Assert.Equal(333, report.FreeBytes);
        Assert.Equal(667, report.UsedBytes);
        Assert.Equal(1000, report.TotalBytes);
        Assert.Equal(33.3, report.PercentFree);
    }

    [Fact]
    public async Task GetSdStorage_OnACardReportingNoCapacity_ReportsZeroPercentRatherThanDividingByIt()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.SdStorage = new SdCardStorageInfo(FreeBytes: 0, TotalBytes: 0);

        var report = await agent.GetSdStorageAsync(AgentHarness.DeviceId, CancellationToken.None);

        Assert.Equal(0, report.PercentFree);
    }

    [Fact]
    public async Task StartSdLogging_PassesTheFormatAndReportsTheNameTheDeviceChose()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 500);

        var result = await agent.StartLoggingAsync(AgentHarness.DeviceId, fileName: null, "csv", CancellationToken.None);

        Assert.Equal(SdCardLogFormat.Csv, device.StartedSession!.Format);
        // Core owns the naming convention, so the tool must report what came back rather than a
        // name it guessed — the two used to be generated independently.
        Assert.Equal(device.StartedSession.FileName, result.FileName);
        Assert.Equal(500, result.SampleRateHz);
        Assert.Equal(new[] { 0, 1 }, result.EnabledAnalogChannels);
    }

    [Fact]
    public async Task StartSdLogging_WithAnUnknownFormat_IsRejectedBeforeTheCardIsTouched()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.StartLoggingAsync(AgentHarness.DeviceId, "log.bin", "parquet", CancellationToken.None));

        Assert.Contains("'protobuf', 'json', or 'csv'", ex.Message);
        Assert.Null(device.StartedSession);
    }

    [Fact]
    public async Task StartSdLogging_WithARateTheChannelSetCannotSustain_RefusesRatherThanRecordNothing()
    {
        // The firmware answers an over-cap rate by refusing and recording zero samples, silently.
        // This is the last point before the wire where an agent can be told.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 20_000);

        // The cap moves under the live rate without any tool call re-validating it — which is the
        // only way an over-cap rate survives to this point, since every configure_* call enforces.
        device.CapForEnabledAnalogCount = _ => 1_000;
        await device.ReadCapabilityDocumentAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.StartLoggingAsync(AgentHarness.DeviceId, "log.bin", "protobuf", CancellationToken.None));

        Assert.Contains("set_sample_rate", ex.Message);
        Assert.Null(device.StartedSession);
    }

    [Fact]
    public async Task StopSdLogging_EndsTheSessionAndTheStatusSaysSo()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.StartLoggingAsync(AgentHarness.DeviceId, "log.bin", "protobuf", CancellationToken.None);
        Assert.True(agent.GetStatus(AgentHarness.DeviceId).LoggingToSdCard);

        var message = await agent.StopLoggingAsync(AgentHarness.DeviceId, CancellationToken.None);

        Assert.Contains("Stopped SD-card logging", message);
        Assert.False(agent.GetStatus(AgentHarness.DeviceId).LoggingToSdCard);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task LoggingTools_AreRefusedInReadOnlyMode(string tool)
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(readOnly: true);

        Task Call() => tool == "start"
            ? agent.StartLoggingAsync(AgentHarness.DeviceId, "log.bin", "protobuf", CancellationToken.None)
            : agent.StopLoggingAsync(AgentHarness.DeviceId, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);
        Assert.Contains("read-only", ex.Message);
        Assert.Null(device.StartedSession);
    }

    [Fact]
    public async Task ShutdownAsync_StopsALiveRecordingBeforeReleasingTheDevice()
    {
        // A serial port released with the card still recording leaves the device writing to a log
        // nothing will ever close.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.StartLoggingAsync(AgentHarness.DeviceId, "log.bin", "protobuf", CancellationToken.None);

        await agent.ShutdownAsync();

        Assert.False(((ISdCardOperations)device).IsLoggingToSdCard);
        Assert.Empty(agent.ListConnected());
    }
}

/// <summary>
/// The tool wrapper itself: <see cref="DaqifiTools"/> exists to turn the agent's exceptions into
/// something the model can read, and that translation had no tests of its own.
/// </summary>
public class ToolErrorTranslationTests
{
    [Fact]
    public void ASynchronousToolFailure_ReachesTheModelAsItsOwnMessage()
    {
        var agent = new DaqifiAgent(new ServerOptions());

        var ex = Assert.Throws<McpException>(() => DaqifiTools.GetDeviceStatus(agent, "serial:NOPE"));

        // Not a generic "An error occurred": the whole point is that "call connect_device first"
        // survives the trip to the model.
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task AnAsynchronousToolFailure_ReachesTheModelAsItsOwnMessage()
    {
        var agent = new DaqifiAgent(new ServerOptions());

        var ex = await Assert.ThrowsAsync<McpException>(
            () => DaqifiTools.SetSampleRate(agent, "serial:NOPE", 100));

        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task ACoreArgumentFailure_IsTranslatedToo()
    {
        // Core's own exception types are not McpException, and an untranslated one is what the
        // model sees as an opaque failure.
        var (agent, _) = AgentHarness.WithConnectedDevice();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => DaqifiTools.SetPwmOutput(agent, AgentHarness.DeviceId, channel: 1, dutyCyclePercent: 50));

        Assert.Contains("does not support PWM", ex.Message);
    }

    [Fact]
    public async Task Cancellation_IsNotDisguisedAsAToolError()
    {
        // A cancelled call is the host giving up, not the device failing; reporting it as an
        // McpException would have the model retry something that was never broken.
        var (agent, _) = AgentHarness.WithConnectedDevice();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DaqifiTools.StopSdLogging(agent, AgentHarness.DeviceId, cts.Token));
    }

    [Fact]
    public async Task ASuccessfulToolCall_IsNotWrappedAtAll()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();

        var result = await DaqifiTools.ConfigureAnalogChannels(agent, AgentHarness.DeviceId, new[] { 0, 1 });

        Assert.Equal(new[] { 0, 1 }, result.EnabledAnalogChannels);
    }
}
