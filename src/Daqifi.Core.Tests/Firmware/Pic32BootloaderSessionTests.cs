using System.IO;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for <see cref="Pic32BootloaderSession"/>, the collaborator that owns the individual
/// HID exchanges with the PIC32 bootloader (part of #464).
/// </summary>
/// <remarks>
/// Deliberately NOT a re-run of <c>FirmwareUpdateServiceTests</c> one level down: the facade suite
/// already covers the PIC32 flow end to end — the happy path, targeting by path/location, the
/// cleanup re-erase outcomes and the soft-reset recovery — and duplicating those here would only
/// mean two places to edit. What lives here is what an exchange decides on its own and the facade
/// either cannot reach or cannot state cleanly: the pre-flight rejection of an unwritable image,
/// the composition and per-run reset of the bootloader-search diagnostic, exact-match targeting,
/// the connect retry policy's boundaries, which verify failures are transport noise and which are
/// a genuinely bad flash, the progress bands each phase reports into, and the cleanup disconnect's
/// duty to stay silent.
/// </remarks>
public class Pic32BootloaderSessionTests
{
    private const byte VersionRequest = 0x11;
    private const byte EraseRequest = 0x22;
    private const byte ProgramRequest = 0x33;
    private const byte ReadCrcRequest = 0x44;
    private const byte JumpRequest = 0x55;

    private static readonly byte[] VersionOk = [0x01, 0x10];
    private static readonly byte[] VersionBad = [0xEE];
    private static readonly byte[] EraseAck = [0x01, 0x02];
    private static readonly byte[] EraseNak = [0x00, 0x00];
    private static readonly byte[] ProgramAck = [0x01, 0x03];

    // ---------------------------------------------------------------------------------------
    // Preparing the HEX image (before any device I/O)
    // ---------------------------------------------------------------------------------------

    public static TheoryData<byte[][]> UnwritableImages => new()
    {
        // The parser found no records at all.
        Array.Empty<byte[]>(),
        // It found records, but none of them carry a byte to program.
        new byte[][] { [] },
        new byte[][] { [], [] }
    };

    [Theory]
    [MemberData(nameof(UnwritableImages))]
    public void PrepareHexImage_WhenTheImageHasNothingToWrite_RefusesBeforeTouchingTheDevice(byte[][] records)
    {
        var transport = new FakeHidTransport();
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol(records));

        var error = Assert.Throws<InvalidDataException>(() => session.PrepareHexImage(["dummy"]));

        Assert.Contains("did not contain any writable records", error.Message, StringComparison.Ordinal);

        // The point of the guard: it runs before the flow can erase a working device for an image
        // that would then program nothing.
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ConnectAttempts);
    }

    [Fact]
    public void PrepareHexImage_ReportsTheRecordsTheirTotalSizeAndTheRegionsToVerify()
    {
        var regions = new[]
        {
            new FlashCrcRegion(0x9D000000, 256, 0xABCD),
            new FlashCrcRegion(0x9D001000, 128, 0x1234)
        };

        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02, 0x03]], regions));

        var (hexRecords, crcRegions, totalBytes) = session.PrepareHexImage(["dummy"]);

        Assert.Equal(2, hexRecords.Count);

        // The byte total is what drives the Programming progress band, so it has to be the sum of
        // the records actually programmed — not the record count, not the file size.
        Assert.Equal(5, totalBytes);
        Assert.Equal(regions, crcRegions);
    }

    // ---------------------------------------------------------------------------------------
    // Finding the bootloader
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WaitForBootloaderDevice_WithNoTargetRequested_TakesTheFirstOneEnumerated()
    {
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new FakeHidDeviceEnumerator([], [Bootloader("path-1"), Bootloader("path-2")]));

        var found = await session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None);

        Assert.Equal("path-1", found.DevicePath);
    }

    [Fact]
    public async Task WaitForBootloaderDevice_KeepsPollingUntilTheBootloaderEnumerates()
    {
        // Re-enumeration after SYSTem:FORceBoot is not instant; an empty first sweep is normal.
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                Array.Empty<HidDeviceInfo>(),
                [Bootloader("path-1")]
            ]));

        var found = await session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None);

        Assert.Equal("path-1", found.DevicePath);
        Assert.Contains("after 3 poll attempt(s)", session.DescribeBootloaderSearch(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForBootloaderDevice_WithATargetPath_NeverAsksWhereTheCandidatesArePluggedIn()
    {
        // Resolving a physical location is a WMI query per candidate per poll on Windows. A path
        // is already a unique identity, so targeting by path must not pay for it.
        var locations = new FakeUsbLocationProvider(new Dictionary<string, string>
        {
            ["path-1"] = "USB(1)",
            ["path-2"] = "USB(2)"
        });

        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new FakeHidDeviceEnumerator([], [Bootloader("path-1"), Bootloader("path-2")]),
            locations);

        var found = await session.WaitForBootloaderDeviceAsync("path-2", null, CancellationToken.None);

        Assert.Equal("path-2", found.DevicePath);
        Assert.Empty(locations.Requests);
    }

    [Fact]
    public async Task WaitForBootloaderDevice_MatchesTheTargetPathExactly_NotIgnoringCase()
    {
        // A device path is an OS identifier and the caller obtained it from the same enumeration
        // this method reads, so a case-folded "match" would connect to a device nobody asked for.
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new FakeHidDeviceEnumerator([], [Bootloader("path-1")]));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.WaitForBootloaderDeviceAsync("PATH-1", null, cts.Token));
    }

    [Fact]
    public async Task WaitForBootloaderDevice_WhenEnumerationFails_SaysWhichAttemptAndKeepsTheCause()
    {
        var cause = new UnauthorizedAccessException("HID enumeration denied.");
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new ThrowingHidDeviceEnumerator(cause));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None));

        // An enumeration fault is not "no bootloader yet" — it is a host-side problem and must not
        // be retried into a silent timeout.
        Assert.Same(cause, error.InnerException);
        Assert.Contains("VID=0x04D8", error.Message, StringComparison.Ordinal);
        Assert.Contains("PID=0x003C", error.Message, StringComparison.Ordinal);
        Assert.Contains("poll attempt 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForBootloaderDevice_WhenCanceled_StopsPolling()
    {
        var enumerator = new CountingHidDeviceEnumerator();
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            enumerator);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.WaitForBootloaderDeviceAsync(null, null, cts.Token));

        var pollsAtCancellation = enumerator.Calls;
        await Task.Delay(100);
        Assert.Equal(pollsAtCancellation, enumerator.Calls);
    }

    // ---------------------------------------------------------------------------------------
    // The bootloader-search diagnostic
    //
    // This string is the whole explanation an operator gets when a device never comes back in
    // bootloader mode, and by then the poll loop is gone. Each clause is asserted because each one
    // answers a different "why" — wrong VID/PID, gave up too early, wrong target, host-side fault.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DescribeBootloaderSearch_BeforeAnyPoll_NamesWhatItWillLookFor()
    {
        var (session, _) = CreateSession(new FakeHidTransport(), new FakeBootloaderProtocol([[0xA1]]));

        var description = session.DescribeBootloaderSearch();

        Assert.Contains("VID=0x04D8", description, StringComparison.Ordinal);
        Assert.Contains("PID=0x003C", description, StringComparison.Ordinal);
        Assert.Contains("after 0 poll attempt(s)", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Target", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Last HID enumeration error", description, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeBootloaderSearch_NamesTheTargetTheCallerAskedFor()
    {
        var (session, _) = CreateSession(new FakeHidTransport(), new FakeBootloaderProtocol([[0xA1]]));

        session.SetRequestedTarget("path-9", "USB(4)");
        var description = session.DescribeBootloaderSearch();

        Assert.Contains("Target device path requested: path-9.", description, StringComparison.Ordinal);
        Assert.Contains("Target location key requested: USB(4).", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeBootloaderSearch_WhenEnumerationFailed_IncludesTheWholeErrorChain()
    {
        var cause = new UnauthorizedAccessException(
            "HID enumeration denied.",
            new InvalidOperationException("Underlying platform refusal."));

        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new ThrowingHidDeviceEnumerator(cause));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None));

        var description = session.DescribeBootloaderSearch();

        Assert.Contains("Last HID enumeration error", description, StringComparison.Ordinal);
        Assert.Contains("HID enumeration denied.", description, StringComparison.Ordinal);
        Assert.Contains("Underlying platform refusal.", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeBootloaderSearch_AfterEnumerationRecovers_DropsTheEarlierError()
    {
        // The soft-reset recovery searches a second time. If the first search's error were still
        // reported, a later timeout would blame a fault that has already cleared.
        var enumerator = new SequencedHidDeviceEnumerator(
            [new UnauthorizedAccessException("HID enumeration denied.")],
            [Bootloader("path-1")]);

        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            enumerator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None));
        Assert.Contains("Last HID enumeration error", session.DescribeBootloaderSearch(), StringComparison.Ordinal);

        await session.WaitForBootloaderDeviceAsync(null, null, CancellationToken.None);

        Assert.DoesNotContain("Last HID enumeration error", session.DescribeBootloaderSearch(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetTargetingState_ClearsThePreviousRunsPollCountTargetAndError()
    {
        // Without this, a second update's timeout message would describe the first update's
        // search — wrong attempt count, a target the caller never asked for this time, and an
        // enumeration error that is no longer current.
        var (session, _) = CreateSession(
            new FakeHidTransport(),
            new FakeBootloaderProtocol([[0xA1]]),
            new ThrowingHidDeviceEnumerator(new UnauthorizedAccessException("HID enumeration denied.")));

        session.SetRequestedTarget("path-9", "USB(4)");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForBootloaderDeviceAsync("path-9", "USB(4)", CancellationToken.None));

        session.ResetTargetingState();
        var description = session.DescribeBootloaderSearch();

        Assert.Contains("after 0 poll attempt(s)", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Target", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Last HID enumeration error", description, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Connecting the HID transport
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Connect_WhenTheTransportIsStillHoldingAHandle_ReleasesItFirst()
    {
        // A live handle from an earlier phase (or an earlier run) is exactly the dirty state that
        // makes a connect fail, so the connect starts by dropping whatever is held.
        var transport = new FakeHidTransport();
        await transport.ConnectAsync(0x04D8, 0x003C);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.ConnectWithRetryAsync(Bootloader("path-1"), null, null, CancellationToken.None);

        Assert.Equal(1, transport.DisconnectCalls);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Connect_WithoutATarget_ConnectsByVidPidAndSerialNumber()
    {
        var transport = new FakeHidTransport();
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.ConnectWithRetryAsync(
            Bootloader("path-1", "SN-7"), null, null, CancellationToken.None);

        Assert.Equal(0, transport.ConnectByPathAttempts);
        Assert.Equal(0x04D8, transport.VendorId);
        Assert.Equal(0x003C, transport.ProductId);
        Assert.Equal("SN-7", transport.SerialNumber);
    }

    [Fact]
    public async Task Connect_WhenOnlyALocationKeyWasRequested_StillConnectsToTheMatchedPath()
    {
        // Location targeting exists because several identical bootloaders can be enumerated with
        // no serial to tell them apart — so the connect has to address the one that matched, and
        // VID/PID first-match would defeat the whole point.
        var transport = new FakeHidTransport();
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.ConnectWithRetryAsync(
            Bootloader("path-2"), null, "USB(2)", CancellationToken.None);

        Assert.Equal(1, transport.ConnectByPathAttempts);
        Assert.Equal("path-2", transport.LastConnectByPath);
    }

    public static TheoryData<Exception> TransientConnectFailures => new()
    {
        new IOException("USB glitch."),
        new TimeoutException("Handle did not open in time."),
        new InvalidOperationException("Device busy.")
    };

    [Theory]
    [MemberData(nameof(TransientConnectFailures))]
    public async Task Connect_RetriesATransientFailureAndSucceeds(Exception transientFailure)
    {
        var transport = new FakeHidTransport();
        transport.ConnectFailures.Enqueue(transientFailure);

        var options = CreateFastOptions();
        options.HidConnectRetryCount = 3;
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        await session.ConnectWithRetryAsync(Bootloader("path-1"), null, null, CancellationToken.None);

        Assert.Equal(2, transport.ConnectAttempts);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Connect_WhenTheTransportCannotAddressByPath_FailsWithoutBurningTheRetryBudget()
    {
        // IHidTransport.ConnectByPathAsync is a default interface method that throws
        // NotSupportedException on transports without path addressing. That is a permanent
        // capability answer, not a USB glitch — retrying it just delays a certain failure.
        var transport = new FakeHidTransport();
        var options = CreateFastOptions();
        options.HidConnectRetryCount = 3;
        for (var i = 0; i < options.HidConnectRetryCount; i++)
        {
            transport.ConnectFailures.Enqueue(
                new NotSupportedException("This IHidTransport implementation does not support path-based addressing."));
        }

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.ConnectWithRetryAsync(Bootloader("path-1"), "path-1", null, CancellationToken.None));

        Assert.Equal(1, transport.ConnectByPathAttempts);
    }

    [Fact]
    public async Task Connect_WhenEveryAttemptFails_SurfacesTheLastFailure()
    {
        var transport = new FakeHidTransport();
        var options = CreateFastOptions();
        options.HidConnectRetryCount = 3;
        transport.ConnectFailures.Enqueue(new IOException("attempt 1"));
        transport.ConnectFailures.Enqueue(new IOException("attempt 2"));
        transport.ConnectFailures.Enqueue(new IOException("attempt 3"));

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        var error = await Assert.ThrowsAsync<IOException>(
            () => session.ConnectWithRetryAsync(Bootloader("path-1"), null, null, CancellationToken.None));

        Assert.Equal("attempt 3", error.Message);
        Assert.Equal(3, transport.ConnectAttempts);
    }

    // ---------------------------------------------------------------------------------------
    // Reading the bootloader version (the health check)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestVersion_AsksOnceAndReturnsWhatTheBootloaderAnswered()
    {
        var transport = ConnectedTransport();
        transport.EnqueueRead(VersionOk);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        var version = await session.RequestVersionAsync(CancellationToken.None);

        Assert.Equal("1.0", version);
        Assert.Equal([VersionRequest], Assert.Single(transport.Writes));
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("error")]
    [InlineData("ERROR")]
    public async Task RequestVersion_WhenTheBootloaderCannotAnswer_RefusesWhateverTheCasing(string decoded)
    {
        // The version read is the health check that gates erasing a working device. "Error" in any
        // casing is the protocol saying it could not decode the frame, and the session must not
        // treat it as a version string just because the spelling differs.
        var transport = ConnectedTransport();
        transport.EnqueueRead(VersionBad);

        var protocol = new FakeBootloaderProtocol([[0xA1]]) { VersionDecoder = _ => decoded };
        var (session, _) = CreateSession(transport, protocol);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.RequestVersionAsync(CancellationToken.None));

        Assert.Contains("invalid version response", error.Message, StringComparison.Ordinal);

        // Deliberately outside the retry wrapper: a bootloader that cannot answer will not answer
        // better on the next try, and the caller has a soft-reset recovery for exactly this.
        Assert.Single(transport.Writes);
    }

    // ---------------------------------------------------------------------------------------
    // Erasing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Erase_WhenTheAcknowledgementIsBad_RetriesUpToTheBudgetThenGivesUp()
    {
        var transport = ConnectedTransport();
        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 3;
        for (var i = 0; i < options.FlashWriteRetryCount; i++)
        {
            transport.EnqueueRead(EraseNak);
        }

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.EraseFlashWithRetryAsync(CancellationToken.None));

        Assert.Contains("erase acknowledgment was invalid", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, transport.Writes.Count(w => w[0] == EraseRequest));
    }

    [Fact]
    public async Task Erase_WhenARetryIsAcknowledged_Succeeds()
    {
        var transport = ConnectedTransport();
        transport.EnqueueRead(EraseNak);
        transport.EnqueueRead(EraseAck);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.EraseFlashWithRetryAsync(CancellationToken.None);

        Assert.Equal(2, transport.Writes.Count(w => w[0] == EraseRequest));
    }

    // ---------------------------------------------------------------------------------------
    // Programming
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Program_WalksTheProgressBandFrom20UpTo90()
    {
        // Programming owns 20→90%; Verifying starts at 92 and JumpingToApp at 95. If this band
        // drifts, a progress bar either stalls or jumps backwards at a phase change.
        var transport = ConnectedTransport();
        var records = new byte[][] { [0xA1, 0xA2], [0xB1, 0xB2], [0xC1, 0xC2, 0xC3, 0xC4] };
        foreach (var _ in records)
        {
            transport.EnqueueRead(ProgramAck);
        }

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol(records));

        var reports = new List<FirmwareUpdateProgress>();
        await session.ProgramFlashAsync(
            records, records.Sum(r => (long)r.Length), new CapturingProgress<FirmwareUpdateProgress>(reports), CancellationToken.None);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Equal(FirmwareUpdateState.Programming, r.State));

        // 2 of 8 bytes written → 20 + 2/8*70.
        Assert.Equal(37.5, reports[0].PercentComplete, 3);
        Assert.Equal(90, reports[^1].PercentComplete, 3);
        Assert.Equal(8, reports[^1].BytesWritten);
        Assert.Equal("Programming record 3 of 3", reports[^1].CurrentOperation);
        Assert.Equal(records.Length, transport.Writes.Count(w => w[0] == ProgramRequest));
    }

    [Fact]
    public async Task Program_WhenTheImageSizeIsUnknown_DoesNotDivideByZero()
    {
        var transport = ConnectedTransport();
        var records = new byte[][] { [0xA1], [0xA2] };
        foreach (var _ in records)
        {
            transport.EnqueueRead(ProgramAck);
        }

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol(records));

        var reports = new List<FirmwareUpdateProgress>();
        await session.ProgramFlashAsync(
            records, totalBytes: 0, new CapturingProgress<FirmwareUpdateProgress>(reports), CancellationToken.None);

        Assert.All(reports, r => Assert.Equal(90, r.PercentComplete, 3));
    }

    [Fact]
    public async Task Program_WhenCanceled_LeavesTheRemainingRecordsUnwritten()
    {
        var transport = ConnectedTransport();
        var records = new byte[][] { [0xA1], [0xA2], [0xA3] };
        transport.EnqueueRead(ProgramAck);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol(records));

        using var cts = new CancellationTokenSource();
        transport.WriteHook = (data, _) =>
        {
            if (data[0] == ProgramRequest)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ProgramFlashAsync(records, 3, null, cts.Token));

        Assert.Equal(1, transport.Writes.Count(w => w[0] == ProgramRequest));
    }

    // ---------------------------------------------------------------------------------------
    // Verifying
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_WithNothingToChecksum_ConfirmsTheBootloaderIsStillAnsweringInstead()
    {
        // A degenerate image leaves no region to CRC. Returning silently would mean jumping to an
        // application over a link we never proved was alive, so the session substitutes a liveness
        // check for the verification it could not do.
        var transport = ConnectedTransport();
        transport.EnqueueRead(VersionOk);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.VerifyFlashContentsAsync([], null, totalBytes: 0, CancellationToken.None);

        Assert.Equal([VersionRequest], Assert.Single(transport.Writes));
    }

    [Fact]
    public async Task Verify_WhenTheDeviceReportsADifferentCrc_FailsWithoutRetrying()
    {
        // A CRC mismatch is deterministic: the flash does not match the image. Retrying would burn
        // the budget re-reading the same wrong answer and delay the cleanup re-erase.
        var transport = ConnectedTransport();
        transport.EnqueueRead([0x34, 0x12]); // device reports 0x1234
        transport.EnqueueRead([0x34, 0x12]); // never read — a retry would consume this

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 3;
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.VerifyFlashContentsAsync(
                [new FlashCrcRegion(0x9D000000, 256, 0xABCD)], null, 256, CancellationToken.None));

        Assert.Contains("expected 0xABCD", error.Message, StringComparison.Ordinal);
        Assert.Contains("device reported 0x1234", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.Writes.Count(w => w[0] == ReadCrcRequest));
    }

    [Fact]
    public async Task Verify_WhenTheCrcFrameIsMalformed_TreatsItAsTransportNoiseAndRetries()
    {
        // The opposite call from the mismatch above: a frame the decoder cannot read says nothing
        // about the flash, so it is retried like any other USB glitch — and if it never clears, it
        // is reported as a transport fault rather than a bad flash.
        var transport = ConnectedTransport();
        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 3;
        for (var i = 0; i < options.FlashWriteRetryCount; i++)
        {
            transport.EnqueueRead([0x99]); // too short for the fake decoder
        }

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]), options: options);

        var error = await Assert.ThrowsAsync<IOException>(
            () => session.VerifyFlashContentsAsync(
                [new FlashCrcRegion(0x9D000000, 256, 0xABCD)], null, 256, CancellationToken.None));

        Assert.Contains("transient transport fault", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(3, transport.Writes.Count(w => w[0] == ReadCrcRequest));
    }

    [Fact]
    public async Task Verify_ReportsTheVerifyingBandUpTo94()
    {
        var transport = ConnectedTransport();
        transport.EnqueueRead([0xCD, 0xAB]);
        transport.EnqueueRead([0x34, 0x12]);

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        var reports = new List<FirmwareUpdateProgress>();
        await session.VerifyFlashContentsAsync(
            [
                new FlashCrcRegion(0x9D000000, 256, 0xABCD),
                new FlashCrcRegion(0x9D001000, 128, 0x1234)
            ],
            new CapturingProgress<FirmwareUpdateProgress>(reports),
            totalBytes: 384,
            CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(FirmwareUpdateState.Verifying, r.State));
        Assert.Equal(93, reports[0].PercentComplete, 3);

        // Stops below JumpingToApp's 95 so the next phase still moves forward.
        Assert.Equal(94, reports[^1].PercentComplete, 3);
    }

    // ---------------------------------------------------------------------------------------
    // Jumping back and letting go
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SendJumpToApplication_WritesTheJumpAndNothingElse()
    {
        // The command that ends every PIC32 run, including the non-destructive diagnostics: it must
        // stay a single write with no erase or program traffic attached.
        var transport = ConnectedTransport();
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.SendJumpToApplicationAsync(CancellationToken.None);

        Assert.Equal([JumpRequest], Assert.Single(transport.Writes));
    }

    [Fact]
    public async Task SafeDisconnect_WhenNothingIsConnected_DoesNothing()
    {
        var transport = new FakeHidTransport();
        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.SafeDisconnectAsync();

        Assert.Equal(0, transport.DisconnectCalls);
    }

    [Fact]
    public async Task SafeDisconnect_WhenTheHandleFailsToClose_SwallowsIt()
    {
        // This runs in the cleanup path of a run that is usually already failing. A throw here
        // would replace the real diagnosis with a close error nobody can act on.
        var transport = ConnectedTransport();
        transport.DisconnectFailure = new IOException("Handle already dead.");

        var (session, _) = CreateSession(transport, new FakeBootloaderProtocol([[0xA1]]));

        await session.SafeDisconnectAsync();

        Assert.Equal(1, transport.DisconnectCalls);
        Assert.False(session.IsConnected);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static (Pic32BootloaderSession Session, FirmwareUpdateContext Context) CreateSession(
        FakeHidTransport transport,
        IBootloaderProtocol protocol,
        IHidDeviceEnumerator? enumerator = null,
        IUsbLocationProvider? locationProvider = null,
        FirmwareUpdateServiceOptions? options = null)
    {
        var context = new FirmwareUpdateContext(
            eventSender: new object(),
            NullLogger.Instance,
            options ?? CreateFastOptions());

        var session = new Pic32BootloaderSession(
            context,
            transport,
            protocol,
            enumerator ?? new FakeHidDeviceEnumerator([], [Bootloader("path-1")]),
            locationProvider ?? new FakeUsbLocationProvider(new Dictionary<string, string>()));

        return (session, context);
    }

    private static FakeHidTransport ConnectedTransport()
    {
        var transport = new FakeHidTransport();
        transport.Connect(0x04D8, 0x003C);
        return transport;
    }

    private static HidDeviceInfo Bootloader(string devicePath, string? serialNumber = null)
        => new(0x04D8, 0x003C, devicePath, serialNumber, "DAQiFi Bootloader");

    private static FirmwareUpdateServiceOptions CreateFastOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        BootloaderResponseTimeout = TimeSpan.FromMilliseconds(250),
        HidConnectRetryDelay = TimeSpan.FromMilliseconds(1),
        FlashWriteRetryDelay = TimeSpan.FromMilliseconds(1),
        PostForceBootDelay = TimeSpan.FromMilliseconds(1),
        PostReconnectStaleHandleDelay = TimeSpan.Zero
    };

    /// <summary>
    /// Never produces a bootloader, and counts how many sweeps it was asked for — the only way to
    /// show that a canceled wait actually stopped polling rather than leaking a loop.
    /// </summary>
    private sealed class CountingHidDeviceEnumerator : IHidDeviceEnumerator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
            int? vendorId = null,
            int? productId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<HidDeviceInfo>>(Array.Empty<HidDeviceInfo>());
        }
    }

    /// <summary>
    /// Throws the scripted failures first, then enumerates normally — models an enumeration fault
    /// that clears between two searches within the same run.
    /// </summary>
    private sealed class SequencedHidDeviceEnumerator(
        IReadOnlyList<Exception> failures,
        IReadOnlyList<HidDeviceInfo> thereafter) : IHidDeviceEnumerator
    {
        private readonly Queue<Exception> _failures = new(failures);

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
            int? vendorId = null,
            int? productId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return _failures.Count > 0
                ? Task.FromException<IReadOnlyList<HidDeviceInfo>>(_failures.Dequeue())
                : Task.FromResult(thereafter);
        }
    }
}
