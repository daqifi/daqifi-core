using System.IO;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for <see cref="Pic32FirmwareUpdater"/>, the collaborator that sequences a PIC32
/// update and decides how a failed one ends (part of #464).
/// </summary>
/// <remarks>
/// Deliberately NOT a re-run of <c>FirmwareUpdateServiceTests</c> one level down: the facade suite
/// already covers the happy path, targeting, cancellation, the three cleanup outcomes and their
/// recovery guidance, and the readiness probe. What lives here is what the facade does not set up
/// or cannot state cleanly — the device preparation preconditions, the soft-reset recovery's two
/// awkward corners (the reset write itself failing, and there being no handle to reset), the
/// cleanup rule's other half (a flash that is already good must not be re-erased), and the
/// progress contract across the whole run.
/// </remarks>
public class Pic32FirmwareUpdaterTests
{
    private const byte VersionRequest = 0x11;
    private const byte EraseRequest = 0x22;
    private const byte ProgramRequest = 0x33;
    private const byte JumpRequest = 0x55;

    private static readonly byte[] VersionOk = [0x01, 0x10];
    private static readonly byte[] EraseAck = [0x01, 0x02];
    private static readonly byte[] ProgramAck = [0x01, 0x03];

    private static readonly byte[][] Records = [[0xA1, 0x01], [0xA1, 0x02]];

    // ---------------------------------------------------------------------------------------
    // Preparing the device
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_WhenTheDeviceIsStreaming_StopsItBeforeForcingTheBootloader()
    {
        // A device that is still pushing stream frames when it is told to reboot into the
        // bootloader is the one case where the caller's own state, not ours, decides whether the
        // handoff is clean — so the updater stops the stream itself rather than assuming.
        var harness = new Harness();
        harness.ScriptHappyPathReads();
        harness.Device.StartStreaming();

        await harness.RunUpdateAsync();

        Assert.False(harness.Device.IsStreaming);
        Assert.Equal("SYSTem:FORceBoot", Assert.Single(harness.Device.SentCommands));
        Assert.Equal(FirmwareUpdateState.Complete, harness.Context.CurrentState);
    }

    [Fact]
    public async Task RunUpdate_WhenTheDeviceIsNotConnected_FailsBeforeSendingForceBoot()
    {
        var harness = new Harness();
        harness.Device.Disconnect();

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => harness.RunUpdateAsync());

        Assert.Equal(FirmwareUpdateState.PreparingDevice, error.FailedState);
        Assert.IsType<InvalidOperationException>(error.InnerException);

        // Nothing was asked of the device and nothing was asked of the bootloader: the run stopped
        // at the precondition.
        Assert.Empty(harness.Device.SentCommands);
        Assert.Empty(harness.Transport.Writes);
        Assert.Contains("connected over USB", error.RecoveryGuidance, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The soft-reset recovery (#298)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_WhenTheSoftResetWriteAlsoFails_SurfacesTheOriginalHealthCheckFailure()
    {
        // The recovery is a best-effort attempt to clear a dirty bootloader handle. When it cannot
        // even issue the reset, the caller must still be told what actually went wrong first —
        // reporting the reset's own write error would send them chasing the wrong fault.
        var harness = new Harness();

        var originalFailure = new IOException("Version request write failed.");
        var resetFailure = new IOException("Soft-reset write failed.");
        var scriptedWriteFailures = new Queue<Exception>([originalFailure, resetFailure]);
        harness.Transport.WriteHook = (_, _) => scriptedWriteFailures.Count > 0
            ? Task.FromException(scriptedWriteFailures.Dequeue())
            : Task.CompletedTask;

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => harness.RunUpdateAsync());

        Assert.Same(originalFailure, error.InnerException);
        Assert.Equal(FirmwareUpdateState.Connecting, error.FailedState);
        Assert.DoesNotContain(resetFailure.Message, error.ToString(), StringComparison.Ordinal);

        // Nothing was erased, so this is not a half-flashed device and must not be described as one.
        Assert.DoesNotContain(harness.Transport.Writes, w => w[0] == EraseRequest);
        Assert.DoesNotContain("half-flashed", error.RecoveryGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheConnectNeverSucceeded_SkipsTheSoftResetWriteAndStillRecovers()
    {
        // With no handle there is nothing to reset — writing anyway would throw on a dead transport
        // and abandon a recovery that still works, because re-enumerating and reconnecting is the
        // part that actually clears this failure.
        var harness = new Harness();
        for (var i = 0; i < harness.Options.HidConnectRetryCount; i++)
        {
            harness.Transport.ConnectFailures.Enqueue(new IOException("Bootloader handle is held elsewhere."));
        }

        harness.ScriptHappyPathReads();

        await harness.RunUpdateAsync();

        Assert.Equal(FirmwareUpdateState.Complete, harness.Context.CurrentState);

        // Exactly one JMP_TO_APP in the whole run: the one that ends it. A soft-reset write on a
        // transport that never connected would show up as a second.
        Assert.Equal(1, harness.Transport.Writes.Count(w => w[0] == JumpRequest));
    }

    // ---------------------------------------------------------------------------------------
    // How a failed run ends
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_WhenTheJumpBackFails_DoesNotReEraseAFlashThatIsAlreadyGood()
    {
        // The re-erase exists so a half-written flash is never left behind. By JumpingToApp the
        // image is programmed and CRC-verified — erasing it would destroy a good flash to fix a
        // device that only needs a power cycle.
        var harness = new Harness();
        harness.ScriptHappyPathReads();
        harness.Transport.WriteHook = (data, _) => data[0] == JumpRequest
            ? Task.FromException(new IOException("Bootloader stopped answering."))
            : Task.CompletedTask;

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => harness.RunUpdateAsync());

        Assert.Equal(FirmwareUpdateState.JumpingToApp, error.FailedState);
        Assert.Equal(1, harness.Transport.Writes.Count(w => w[0] == EraseRequest));
        Assert.DoesNotContain(FirmwareUpdateState.CleaningUp, harness.StateTransitions);
        Assert.DoesNotContain(FirmwareUpdateState.Recovered, harness.StateTransitions);
        Assert.Equal(FirmwareUpdateState.Failed, harness.StateTransitions[^1]);

        // The guidance is the plain per-state advice, not a cleanup verdict in either direction.
        Assert.Contains("Power-cycle the device", error.RecoveryGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("half-flashed", error.RecoveryGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("clean bootloader state", error.RecoveryGuidance, StringComparison.Ordinal);

        // Whatever the outcome, the HID handle goes back.
        Assert.False(harness.Transport.IsConnected);
    }

    [Fact]
    public async Task RunUpdate_OnSuccess_ReleasesTheHidHandle()
    {
        // The bootloader handle is exclusive: holding it after the device is back in application
        // mode blocks the next update and any other program that wants the device.
        var harness = new Harness();
        harness.ScriptHappyPathReads();

        await harness.RunUpdateAsync();

        Assert.False(harness.Transport.IsConnected);
        Assert.True(harness.Transport.DisconnectCalls >= 1);
        Assert.True(harness.Device.IsConnected);
    }

    // ---------------------------------------------------------------------------------------
    // What a progress sink sees
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_ReportsProgressThatOnlyEverMovesForward()
    {
        // Each phase writes into its own band and the bands are assigned by hand across two files.
        // A progress bar that jumps backwards mid-flash reads as a fault to the operator, so the
        // whole run is checked here rather than band by band.
        var harness = new Harness();
        harness.ScriptHappyPathReads();

        var reports = new List<FirmwareUpdateProgress>();
        await harness.RunUpdateAsync(new CapturingProgress<FirmwareUpdateProgress>(reports));

        Assert.Equal(0, reports[0].PercentComplete);
        Assert.Equal(100, reports[^1].PercentComplete);
        Assert.Equal(FirmwareUpdateState.Complete, reports[^1].State);

        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(
                reports[i].PercentComplete >= reports[i - 1].PercentComplete,
                $"Progress went backwards at report {i} ({reports[i - 1].State} " +
                $"{reports[i - 1].PercentComplete}% → {reports[i].State} {reports[i].PercentComplete}%).");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A PIC32 updater wired to fakes, with the whole flow's collaborators exposed so a test can
    /// script one failure and assert on the rest.
    /// </summary>
    private sealed class Harness
    {
        internal Harness()
        {
            Options = new FirmwareUpdateServiceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                PreparingDeviceTimeout = TimeSpan.FromSeconds(5),
                WaitingForBootloaderTimeout = TimeSpan.FromSeconds(5),
                ConnectingTimeout = TimeSpan.FromSeconds(5),
                ErasingFlashTimeout = TimeSpan.FromSeconds(5),
                ProgrammingTimeout = TimeSpan.FromSeconds(5),
                VerifyingTimeout = TimeSpan.FromSeconds(5),
                JumpingToApplicationTimeout = TimeSpan.FromSeconds(5),
                BootloaderResponseTimeout = TimeSpan.FromMilliseconds(250),
                PostForceBootDelay = TimeSpan.FromMilliseconds(1),
                HidConnectRetryDelay = TimeSpan.FromMilliseconds(1),
                FlashWriteRetryDelay = TimeSpan.FromMilliseconds(1),

                // The fake device does not reproduce the macOS USB CDC re-enumeration race, so the
                // stale-handle dance would be pure latency here.
                PostReconnectStaleHandleDelay = TimeSpan.Zero
            };

            Context = new FirmwareUpdateContext(eventSender: new object(), NullLogger.Instance, Options);
            Context.StateChanged += (_, args) => StateTransitions.Add(args.CurrentState);

            Session = new Pic32BootloaderSession(
                Context,
                Transport,
                Protocol,
                new FakeHidDeviceEnumerator([], [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]),
                new FakeUsbLocationProvider(new Dictionary<string, string>()));

            Context.WaitingForBootloaderTimeoutDetailProvider = Session.DescribeBootloaderSearch;
            Updater = new Pic32FirmwareUpdater(Context, Session);
        }

        internal FirmwareUpdateServiceOptions Options { get; }

        internal FirmwareUpdateContext Context { get; }

        internal FakeStreamingDevice Device { get; } = new("COM3");

        internal FakeHidTransport Transport { get; } = new();

        internal FakeBootloaderProtocol Protocol { get; } = new(Records);

        internal Pic32BootloaderSession Session { get; }

        internal Pic32FirmwareUpdater Updater { get; }

        internal List<FirmwareUpdateState> StateTransitions { get; } = [];

        /// <summary>
        /// Queues the HID responses a clean run consumes: the connect health check, the erase
        /// acknowledgment, one acknowledgment per record, and — because this image has no CRC
        /// regions — the liveness version read the verify step substitutes for a checksum.
        /// </summary>
        internal void ScriptHappyPathReads()
        {
            Transport.EnqueueRead(VersionOk);
            Transport.EnqueueRead(EraseAck);
            foreach (var _ in Records)
            {
                Transport.EnqueueRead(ProgramAck);
            }

            Transport.EnqueueRead(VersionOk);
        }

        internal Task RunUpdateAsync(IProgress<FirmwareUpdateProgress>? progress = null)
        {
            return Updater.RunUpdateAsync(
                Device,
                Records,
                crcRegions: [],
                totalBytes: Records.Sum(r => (long)r.Length),
                progress,
                targetDevicePath: null,
                targetLocationKey: null,
                CancellationToken.None);
        }
    }
}
