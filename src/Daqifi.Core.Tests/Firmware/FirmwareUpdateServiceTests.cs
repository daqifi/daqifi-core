using System.IO;
using System.Net;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

public class FirmwareUpdateServiceTests
{
    [Fact]
    public async Task UpdateFirmwareAsync_HappyPath_TransitionsThroughExpectedStatesAndReportsProgress()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x01, 0x10]); // verify version

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [
                [0xA1, 0x01],
                [0xA1, 0x02]
            ]);

        var options = CreateFastOptions();
        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath, progress);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(
            [
                FirmwareUpdateState.PreparingDevice,
                FirmwareUpdateState.WaitingForBootloader,
                FirmwareUpdateState.Connecting,
                FirmwareUpdateState.ErasingFlash,
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Verifying,
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.Complete
            ],
            stateTransitions);

        Assert.Equal("SYSTem:FORceBoot", Assert.Single(device.SentCommands));
        Assert.Equal(6, hidTransport.Writes.Count);
        Assert.Equal(1, device.DisconnectCalls);
        Assert.True(device.ConnectAttempts >= 1);

        var terminalProgress = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, terminalProgress.PercentComplete);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenFlashCrcMatches_VerifiesViaReadCrcAndCompletes()
    {
        // Closes #213: the Verifying state issues READ_CRC per region and
        // compares against the host-computed CRC. A matching CRC must pass
        // verification and complete the update.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0xCD, 0xAB]); // READ_CRC response → decodes to 0xABCD (match)

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            CreateFastOptions());

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Contains(FirmwareUpdateState.Verifying, stateTransitions);
        // The fake encodes READ_CRC commands with a leading 0x44 marker; with a
        // region configured, the version-liveness fallback is NOT used.
        Assert.Contains(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x44);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenFlashCrcMismatches_ReErasesAndRecoversToCleanBootloaderState()
    {
        // Closes #208 (with #213): a bit-flipped / partially-programmed flash is
        // detected by the CRC compare in Verifying. Rather than abandoning the
        // device half-flashed, the service re-erases the application flash
        // (CleaningUp → Recovered), leaving a clean bootloader state that can be
        // re-flashed. The update still fails (the firmware was not installed),
        // but the device is safe.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x34, 0x12]); // READ_CRC response → 0x1234 (mismatch vs 0xABCD)
        hidTransport.EnqueueRead([0x01, 0x02]); // cleanup re-erase ack

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            CreateFastOptions());

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath, progress));
        }
        finally
        {
            File.Delete(hexPath);
        }

        // FailedState reports where it actually broke; terminal state is Recovered.
        Assert.Equal(FirmwareUpdateState.Verifying, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);

        var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("CRC mismatch", inner.Message);

        // Cleanup must be observable as CleaningUp → Recovered, in that order.
        Assert.Contains(FirmwareUpdateState.CleaningUp, stateTransitions);
        Assert.Contains(FirmwareUpdateState.Recovered, stateTransitions);
        Assert.True(
            stateTransitions.IndexOf(FirmwareUpdateState.CleaningUp)
            < stateTransitions.IndexOf(FirmwareUpdateState.Recovered));
        Assert.DoesNotContain(FirmwareUpdateState.Failed, stateTransitions);

        // Two erase commands: the original erase plus the cleanup re-erase.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        // Must NOT have jumped to the application after a failed verification.
        Assert.DoesNotContain(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x55);

        // Recovery guidance tells the operator the device is safe to re-flash.
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("clean bootloader state", exception.RecoveryGuidance);

        // Both cleanup states are surfaced via progress, too.
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.CleaningUp);
        var recoveredProgress = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Recovered);
        // Recovered must NOT report 100% (it is not success — that would let a
        // percent-only UI misread cleanup as a completed install) and must NOT
        // reset to 0 (it freezes at the failure point).
        Assert.True(recoveredProgress.PercentComplete is > 0 and < 100);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenProgrammingFails_ReErasesAndRecovers()
    {
        // #208: a failure during Programming (flash already partly written, HID
        // still connected) re-erases to a clean bootloader state.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x00]);       // program ack 1 → invalid (non-retryable at retry count 1)
        hidTransport.EnqueueRead([0x01, 0x02]); // cleanup re-erase ack

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1; // fail fast so Programming terminates

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        Assert.DoesNotContain(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x55);
        Assert.Contains("clean bootloader state", exception.RecoveryGuidance);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenErasingFlashFails_ReErasesAndRecovers()
    {
        // #208: even an ErasingFlash failure is cleanup-eligible — re-erasing
        // leaves a clean bootloader state.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x00]);       // erase ack → invalid (non-retryable at retry count 1)
        hidTransport.EnqueueRead([0x01, 0x02]); // cleanup re-erase ack

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1;

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.ErasingFlash, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);
        // Original (failed) erase + cleanup re-erase = two erase commands.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCleanupReEraseAlsoFails_TransitionsToFailedNotStuck()
    {
        // #208: if the cleanup re-erase itself fails, the device may be
        // half-flashed — the service must end in Failed (not stuck in CleaningUp)
        // and the guidance must warn the operator to power-cycle and retry.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x34, 0x12]); // READ_CRC → mismatch
        hidTransport.EnqueueRead([0x00]);       // cleanup re-erase ack → invalid → cleanup fails

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1; // cleanup erase fails fast on the bad ack

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        // Original failure context preserved; terminal state is Failed (cleanup failed).
        Assert.Equal(FirmwareUpdateState.Verifying, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("CRC mismatch", inner.Message);

        // We attempted cleanup (CleaningUp seen) then fell through to Failed.
        Assert.Contains(FirmwareUpdateState.CleaningUp, stateTransitions);
        Assert.Equal(FirmwareUpdateState.Failed, stateTransitions[^1]);
        Assert.DoesNotContain(FirmwareUpdateState.Recovered, stateTransitions);

        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("half-flashed", exception.RecoveryGuidance);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenFailureBeforeFlashTouched_SkipsCleanup()
    {
        // #208: failures before any flash write (here WaitingForBootloader) are
        // NOT cleanup-eligible — the device was never touched, so the service
        // must go straight to Failed without a CleaningUp/Recovered detour.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(180);
        options.PollInterval = TimeSpan.FromMilliseconds(25);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.DoesNotContain(FirmwareUpdateState.CleaningUp, stateTransitions);
        Assert.DoesNotContain(FirmwareUpdateState.Recovered, stateTransitions);
        // No flash erase was ever issued.
        Assert.DoesNotContain(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x22);
        // Guidance is the per-state advice, not the cleanup/half-flashed text.
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.DoesNotContain("clean bootloader state", exception.RecoveryGuidance);
        Assert.DoesNotContain("half-flashed", exception.RecoveryGuidance);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenReadCrcResponseMalformed_RetriesThenCompletes()
    {
        // A transiently malformed READ_CRC frame (decoder throws
        // InvalidDataException for framing/CRC issues) must be retried like a
        // HID read glitch — consistent with the erase/program steps — rather
        // than failing the whole update. Only a real CRC mismatch fails fast.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x00]);       // malformed READ_CRC response (first attempt)
        hidTransport.EnqueueRead([0xCD, 0xAB]); // valid READ_CRC → 0xABCD (retry)

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 2;
        options.FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        // Two READ_CRC writes: the malformed first attempt plus the retry.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x44));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_VerifiesEveryCrcRegion()
    {
        // Multiple contiguous runs (e.g. split by a calibration gap) each get
        // their own READ_CRC round-trip.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x11, 0x00]); // region 1 → 0x0011
        hidTransport.EnqueueRead([0x22, 0x00]); // region 2 → 0x0022

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions:
            [
                new FlashCrcRegion(0x9D000000, 16, 0x0011),
                new FlashCrcRegion(0x9D000010, 16, 0x0022)
            ]);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            CreateFastOptions());

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x44));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenBootloaderNeverAppears_ThrowsFirmwareUpdateExceptionWithStateContext()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(180);
        options.PollInterval = TimeSpan.FromMilliseconds(25);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.NotNull(exception.RecoveryGuidance);

        var timeoutException = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("No matching HID bootloader device was enumerated", timeoutException.Message);
        Assert.Contains("VID=0x04D8", timeoutException.Message);
        Assert.Contains("PID=0x003C", timeoutException.Message);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCanceled_ThrowsOperationCanceledExceptionAndTransitionsToFailed()
    {
        // Cancellation BEFORE flash is touched (here during WaitingForBootloader)
        // is not cleanup-eligible — nothing to re-erase — so it ends in Failed.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);
        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromSeconds(5);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var hexPath = CreateTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateFirmwareAsync(device, hexPath, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCanceledMidFlash_ReErasesAndRecovers()
    {
        // #208 acceptance criterion: a cancel mid-flash still leaves the device
        // half-flashed, so the service must re-erase to a clean bootloader state
        // (Recovered) before surfacing the cancellation — never strand the device.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x02]); // cleanup re-erase ack

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            CreateFastOptions());

        // Deterministically cancel the moment Programming begins: StateChanged
        // fires synchronously inside the transition, before the first record is
        // written, so the next per-record token check throws OCE.
        using var cts = new CancellationTokenSource();
        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) =>
        {
            stateTransitions.Add(args.CurrentState);
            if (args.CurrentState == FirmwareUpdateState.Programming)
            {
                cts.Cancel();
            }
        };

        var hexPath = CreateTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateFirmwareAsync(device, hexPath, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);
        Assert.Contains(FirmwareUpdateState.CleaningUp, stateTransitions);
        Assert.Contains(FirmwareUpdateState.Recovered, stateTransitions);
        // Original erase + cleanup re-erase = two erase commands; no jump.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        Assert.DoesNotContain(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x55);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenProgramAckFails_RetriesAndCompletes()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x00]);       // invalid program ack (first attempt)
        hidTransport.EnqueueRead([0x01, 0x03]); // retry program ack (record 1)
        hidTransport.EnqueueRead([0x01, 0x03]); // record 2
        hidTransport.EnqueueRead([0x01, 0x10]); // verify version

        var enumerator = new FakeHidDeviceEnumerator([
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [
                [0xA1, 0x01],
                [0xA1, 0x02]
            ]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 2;
        options.FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(7, hidTransport.Writes.Count); // request version + erase + 3 program sends + verify + jump
        Assert.Equal(3, hidTransport.Writes.Count(write => write.Length > 0 && write[0] == 0x33));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenHidEnumerationThrows_ThrowsFirmwareUpdateExceptionWithEnumerationContext()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new ThrowingHidDeviceEnumerator(new InvalidOperationException("HID backend unavailable."));
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);
        var options = CreateFastOptions();

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);

        var hidEnumerationException = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("HID enumeration failed while searching for bootloader device", hidEnumerationException.Message);
        Assert.Contains("VID=0x04D8", hidEnumerationException.Message);
        Assert.Contains("PID=0x003C", hidEnumerationException.Message);
        Assert.NotNull(hidEnumerationException.InnerException);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_HappyPath_SendsExpectedCommandsAndReportsProgressFromProcessOutput()
    {
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["begin write operation", "67%", "begin verify operation", "verify passed", "Operation completed successfully"],
                [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(
            [
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:ENAbled 1",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:COMMunicate:LAN:SAVE"
            ],
            device.SentCommands);

        Assert.Equal(1, device.DisconnectCalls);
        Assert.True(device.ConnectAttempts >= 1);

        Assert.NotNull(externalProcessRunner.LastRequest);
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.Programming && p.PercentComplete > 20);
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.Complete && p.PercentComplete == 100);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenProcessFails_ThrowsFirmwareUpdateExceptionWithProgrammingState()
    {
        var device = new FakeStreamingDevice("COM8");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                1,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["some output"],
                ["fatal"])
        };

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenToolExitsZeroButNeverReportsSuccess_FailsFromOutput()
    {
        // The port-not-released / "false success" case: the WINC tool couldn't open the serial
        // port, produced no programming output, and exited 0. Success is verified from the tool's
        // output (the success marker), NOT its exit code, so this must fail rather than report done.
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, timedOut: false, TimeSpan.FromSeconds(30), [], [])
        };

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
            // No retry for a non-transient failure (no bridge-init markers).
            Assert.Equal(1, externalProcessRunner.RunCount);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenProgrammingFailsWithoutBridgeMarkers_DoesNotRetry()
    {
        // A genuine programming failure (no bridge-init markers) must NOT be retried — retrying
        // only delays the real error and needlessly re-fires bridge activation.
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                1,
                timedOut: false,
                TimeSpan.FromSeconds(30),
                ["software WINC serial bridge found", "Programming device failed"],
                [])
        };

        var options = CreateFastOptions();
        options.WifiFlashAttempts = 2; // retry is allowed but must not trigger for this failure

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));
            Assert.Equal(1, externalProcessRunner.RunCount);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenTransientBridgeFailureThenSuccess_RetriesAndCompletes()
    {
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner();
        // Attempt 1: device hadn't settled into bridge mode — transient failure markers, exit 1.
        externalProcessRunner.ResultSequence.Enqueue(new ExternalProcessResult(
            1,
            timedOut: false,
            TimeSpan.FromSeconds(5),
            ["software WINC serial bridge found", "Programming device failed"],
            ["error: failed to read serial bridge ID query response"]));
        // Attempt 2: succeeds.
        externalProcessRunner.ResultSequence.Enqueue(new ExternalProcessResult(
            0,
            timedOut: false,
            TimeSpan.FromSeconds(40),
            ["begin write operation", "verify passed", "Operation completed successfully"],
            []));

        var options = CreateFastOptions();
        options.WifiFlashAttempts = 2;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(2, externalProcessRunner.RunCount);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_FiresBridgeActivationCallbackAtWincPrompt()
    {
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromSeconds(40),
                ["Power cycle WINC and set to bootloader mode", "verify passed", "Operation completed successfully"],
                [])
        };

        var bridgeActivations = 0;
        var options = CreateFastOptions();
        options.WifiBridgeActivationCallback = () => bridgeActivations++;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(1, bridgeActivations);
    }

    [Fact]
    public void WifiFlashProgressParser_IgnoresImageBuildPercentAndAdvancesAcrossDeviceFlashPhases()
    {
        var parser = new FirmwareUpdateService.WifiFlashProgressParser();

        // The local image-build phase reaches 100% per region; those must NOT move the bar,
        // otherwise the monotonic max latches before the on-device flash even starts.
        Assert.Null(parser.Observe("written 235472 of 237036 bytes to image (100%)"));
        Assert.Null(parser.Observe("written 77304 of 765952 bytes to image (11%)"));

        // Device flash phases advance the bar, each monotonically forward.
        var writeStart = parser.Observe("begin write operation");
        Assert.NotNull(writeStart);

        var writeMid = parser.Observe(" 0x040000:[wwwwwwww] 0x048000:[wwwwwwww] 0x050000:[wwwwwwww] 0x058000:[wwwwwwww]");
        Assert.NotNull(writeMid);
        Assert.True(writeMid > writeStart);

        var readStart = parser.Observe("begin read operation");
        Assert.NotNull(readStart);
        Assert.True(readStart >= writeMid);

        Assert.Null(parser.Observe("verify range 0x000000 to 0x080000"));
        var verifyStart = parser.Observe("begin verify operation");
        Assert.NotNull(verifyStart);
        Assert.True(verifyStart >= readStart);

        var verifyEnd = parser.Observe(" 0x060000:[vvvvvvvv] 0x068000:[vvvvvvvv] 0x070000:[vvvvvvvv] 0x078000:[vvvvvvvv]");
        Assert.NotNull(verifyEnd);
        Assert.True(verifyEnd > verifyStart);
        Assert.True(verifyEnd <= 100);
    }

    [Fact]
    public void WifiFlashProgressParser_MeasuresFromRangeBase_ForNonZeroVerifyRange()
    {
        var parser = new FirmwareUpdateService.WifiFlashProgressParser();

        // Range base 0x40000 (span 0x40000). Absolute block addresses must be measured relative to
        // the base; otherwise the first block saturates the fraction to ~100% immediately.
        parser.Observe("verify range 0x040000 to 0x080000");
        var verifyStart = parser.Observe("begin verify operation");
        Assert.NotNull(verifyStart);

        var firstBlock = parser.Observe(" 0x040000:[vvvvvvvv]");
        Assert.NotNull(firstBlock);

        var lastBlock = parser.Observe(" 0x078000:[vvvvvvvv]");
        Assert.NotNull(lastBlock);

        // The bar advances across the range (the first block was not already saturated).
        Assert.True(lastBlock > firstBlock);
        Assert.True(lastBlock <= 100);
    }

    [Fact]
    public void WifiFlashProgressParser_NeverMovesBackward_WhenAddressesResetBetweenPhases()
    {
        var parser = new FirmwareUpdateService.WifiFlashProgressParser();

        parser.Observe("begin write operation");
        var writeEnd = parser.Observe(" 0x060000:[wwwwwwww] 0x068000:[wwwwwwww] 0x070000:[wwwwwwww] 0x078000:[wwwwwwww]");
        Assert.NotNull(writeEnd);

        // Read restarts addresses at 0x0; the reported percent must not regress below the write end.
        parser.Observe("begin read operation");
        var readLow = parser.Observe(" 0x000000:[rrrrrrrr]");
        if (readLow.HasValue)
        {
            Assert.True(readLow.Value >= writeEnd.Value);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenDeviceVersionMatchesLatest_SkipsFlashAndReportsComplete()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };

        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease };
        var device = new FakeLanChipInfoStreamingDevice("COM9", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        // Device should NOT have been put into firmware update mode
        Assert.DoesNotContain("SYSTem:COMMUnicate:LAN:FWUpdate", device.SentCommands);
        // Progress should contain a Complete event at 100%
        var completeEvent = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, completeEvent.PercentComplete);
        Assert.Contains("already up to date", completeEvent.CurrentOperation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenDeviceVersionIsOlder_ProceedsWithFlash()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 6, 1, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };

        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease };
        var device = new FakeLanChipInfoStreamingDevice("COM10", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        // Device SHOULD have been put into firmware update mode
        Assert.Contains("SYSTem:COMMUnicate:LAN:FWUpdate", device.SentCommands);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenDeviceDoesNotSupportLanQuery_ProceedsWithFlash()
    {
        // FakeStreamingDevice does NOT implement ILanChipInfoProvider — version check is skipped
        var device = new FakeStreamingDevice("COM11");

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Contains("SYSTem:COMMUnicate:LAN:FWUpdate", device.SentCommands);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectReadinessProbe_AwaitedBeforeComplete()
    {
        // Closes #145: serial reconnect succeeds before the application
        // firmware is ready to answer protobuf status queries. The
        // optional readiness probe gives Core a way to wait for true
        // application readiness — when set, no caller needs to
        // reimplement that retry loop.
        //
        // The probe here returns false on the first 2 attempts and true
        // on the 3rd, simulating a slow PIC32 application boot. Without
        // the wait, the update would Complete immediately after serial
        // reopens; with the wait, Complete is held back until the probe
        // succeeds.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var probeCallCount = 0;
        var options = CreateFastOptions();
        options.PostReconnectReadinessProbe = (_, _) =>
        {
            probeCallCount++;
            return Task.FromResult(probeCallCount >= 3);
        };
        // Readiness budget must be strictly less than JumpingToApplicationTimeout
        // (CreateFastOptions uses 2s) — the probe succeeds within 50ms anyway.
        options.PostReconnectReadinessTimeout = TimeSpan.FromSeconds(1);
        options.PostReconnectReadinessRetryDelay = TimeSpan.FromMilliseconds(10);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(3, probeCallCount);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectReadinessProbe_TimeoutFailsTheUpdate()
    {
        // When the application never becomes ready within the configured
        // budget, the update must transition to Failed with a clear
        // timeout message — NOT silently complete and hand back a
        // half-ready device. That's the entire reason for #145.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var options = CreateFastOptions();
        // Probe always returns false → never ready → must time out
        options.PostReconnectReadinessProbe = (_, _) => Task.FromResult(false);
        options.PostReconnectReadinessTimeout = TimeSpan.FromMilliseconds(150);
        options.PostReconnectReadinessRetryDelay = TimeSpan.FromMilliseconds(10);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException ex;
        try
        {
            ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.JumpingToApp, ex.FailedState);
        var inner = Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("application-ready", inner.Message);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectProbeThrowsOwnOCE_RetriesAndCompletes()
    {
        // The probe may legitimately throw OperationCanceledException on its
        // own (e.g. its internal CTS expired) without our timeoutCts firing.
        // That must NOT crash the update loop — it should be treated as a
        // probe failure and retried, just like any other thrown exception.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var probeCallCount = 0;
        var options = CreateFastOptions();
        options.PostReconnectReadinessProbe = (_, _) =>
        {
            probeCallCount++;
            // First two attempts: probe throws its own OCE (e.g. internal CTS).
            // Third attempt: probe returns ready.
            return probeCallCount < 3
                ? Task.FromException<bool>(new OperationCanceledException("probe-internal-cancel"))
                : Task.FromResult(true);
        };
        options.PostReconnectReadinessTimeout = TimeSpan.FromSeconds(1);
        options.PostReconnectReadinessRetryDelay = TimeSpan.FromMilliseconds(10);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(3, probeCallCount);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectProbeThrowsThenFalseThenTimeout_DoesNotAttachStaleInner()
    {
        // The probe throws once, then returns false until the readiness budget
        // expires. The TimeoutException must NOT carry the stale exception
        // from attempt 1 as InnerException — a successful probe call (true OR
        // false) should clear the captured exception.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var probeCallCount = 0;
        var options = CreateFastOptions();
        options.PostReconnectReadinessProbe = (_, _) =>
        {
            probeCallCount++;
            // Attempt 1: throw. Subsequent attempts: return false until timeout.
            return probeCallCount == 1
                ? Task.FromException<bool>(new InvalidOperationException("transient-probe-error"))
                : Task.FromResult(false);
        };
        options.PostReconnectReadinessTimeout = TimeSpan.FromMilliseconds(150);
        options.PostReconnectReadinessRetryDelay = TimeSpan.FromMilliseconds(10);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException ex;
        try
        {
            ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        var inner = Assert.IsType<TimeoutException>(ex.InnerException);
        // Critical: the InvalidOperationException from attempt 1 must NOT
        // be attached — the successful (false) probe in attempt 2+ cleared
        // lastProbeException. InnerException.InnerException should be null.
        Assert.Null(inner.InnerException);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_NoPostReconnectProbe_PreservesLegacyBehavior()
    {
        // No probe configured == legacy behavior: Complete fires as
        // soon as serial reopens. Belt-and-suspenders test that the
        // new code path is fully opt-in.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var options = CreateFastOptions();
        // No PostReconnectReadinessProbe set - legacy path

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectStaleHandleDelay_TriggersExtraDisconnectReconnect()
    {
        // After PIC32 reset on macOS, the first SerialPort.Open() to succeed
        // inside the USB CDC re-enum window is a "shadow" handle — open but
        // bytes don't flow. The fix: close + brief settling delay + reopen
        // to obtain a clean kernel binding. Validate the dance runs:
        // expect TWO disconnect calls and TWO reconnect cycles inside
        // JumpToApplicationAndReconnectAsync (the original after FORCEBOOT,
        // plus the extra one introduced by the stale-handle workaround).
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var options = CreateFastOptions();
        options.PostReconnectStaleHandleDelay = TimeSpan.FromMilliseconds(20);

        var disconnectsBeforeUpdate = device.DisconnectCalls;
        var connectAttemptsBeforeUpdate = device.ConnectAttempts;

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        // Without the dance: 1 disconnect (FORCEBOOT) + 1 connect (reconnect after JumpToApp).
        // With the dance:    2 disconnects (FORCEBOOT + stale-handle discard)
        //                    + 2 connects (initial reconnect + clean re-open).
        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(disconnectsBeforeUpdate + 2, device.DisconnectCalls);
        Assert.Equal(connectAttemptsBeforeUpdate + 2, device.ConnectAttempts);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_PostReconnectStaleHandleDelayZero_SkipsExtraDisconnectReconnect()
    {
        // Opt-out path: setting PostReconnectStaleHandleDelay to Zero
        // (e.g. on Windows where the first open is already clean) must
        // skip the extra disconnect/reconnect cycle entirely.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var options = CreateFastOptions(); // CreateFastOptions sets this to Zero

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        // 1 FORCEBOOT disconnect + 1 reconnect; no stale-handle dance.
        Assert.Equal(1, device.DisconnectCalls);
        Assert.Equal(1, device.ConnectAttempts);
    }

    [Fact]
    public void Constructor_ProbeWithReadinessTimeoutGteJumpToApp_Throws()
    {
        // The whole JumpToApp step is bounded by JumpingToApplicationTimeout
        // via the outer state-timeout. If the readiness budget meets or
        // exceeds it, the outer timeout fires first and surfaces a generic
        // JumpingToApp error instead of the readiness-specific one. Validate()
        // must reject the misconfiguration up front.
        var options = CreateFastOptions();
        options.PostReconnectReadinessProbe = (_, _) => Task.FromResult(true);
        options.JumpingToApplicationTimeout = TimeSpan.FromSeconds(2);
        options.PostReconnectReadinessTimeout = TimeSpan.FromSeconds(2);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01]]),
            new FakeHidDeviceEnumerator([Array.Empty<HidDeviceInfo>()]),
            options));

        Assert.Equal(nameof(FirmwareUpdateServiceOptions.PostReconnectReadinessTimeout), ex.ParamName);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenVersionMatches_ReturnsUpToDate()
    {
        // Closes #143: callers can now make the version decision themselves
        // without triggering UpdateWifiModuleAsync's hidden internal probe.
        // This test asserts the new public planning method returns the
        // expected status object and DOES NOT mutate service state.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice("COM9", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.True(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
        Assert.NotNull(status.CurrentChipInfo);
        Assert.Equal("19.5.4", status.CurrentChipInfo!.FwVersion);
        Assert.NotNull(status.LatestRelease);
        Assert.Equal("19.5.4", status.LatestRelease!.TagName);

        // The planning method must NOT mutate service state (the internal
        // IsWifiFirmwareUpToDateAsync transitions to Complete on its
        // hit-path; CheckWifiFirmwareStatusAsync must not, so callers can
        // call it freely without locking out a subsequent flash.
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenVersionOlder_ReturnsUpdateAvailable()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 6, 1, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice("COM10", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.UpdateAvailable, status.Reason);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenDeviceDoesNotSupportLanQuery_ReturnsDeviceDoesNotSupportLanQuery()
    {
        var device = new FakeStreamingDevice("COM11");

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.DeviceDoesNotSupportLanQuery, status.Reason);
        Assert.Null(status.CurrentChipInfo);
        Assert.Null(status.LatestRelease);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WithSkipVersionCheck_BypassesProbeAndAlwaysFlashes()
    {
        // The motivating case for #143: caller already made the version
        // decision (via CheckWifiFirmwareStatusAsync). Pass skipVersionCheck:true
        // so Core does NOT re-probe the device — even when the device's
        // current version equals the latest, the flash flow runs.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice("COM12", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4", // Matches latest — would normally short-circuit
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(
                device,
                firmwareDir,
                progress: null,
                skipVersionCheck: true);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        // Even though device version matched latest, flash ran because
        // skipVersionCheck bypassed the probe.
        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Contains("SYSTem:COMMUnicate:LAN:FWUpdate", device.SentCommands);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_ReentrantCallFromUpdateProgressCallback_DoesNotDeadlock()
    {
        // Closes a Qodo finding on PR #198: UpdateWifiModuleAsync holds
        // _operationLock while synchronously firing progress.Report().
        // A handler that calls back into CheckWifiFirmwareStatusAsync
        // would deadlock waiting for the same lock the update flow owns
        // (SemaphoreSlim is not re-entrant). The AsyncLocal _isInsideOperation
        // flag detects this case and skips the second acquisition.
        //
        // The test invokes CheckWifiFirmwareStatusAsync from inside a
        // progress callback fired by UpdateWifiModuleAsync. Without the
        // re-entrancy guard, this hangs until xunit's per-test budget
        // kills it. With the guard, the inner call returns quickly and
        // the update completes normally.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 6, 1, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice("COM13", chipInfo: new LanChipInfo
        {
            ChipId = 1234,
            FwVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        WifiFirmwareStatus? reentrantStatus = null;
        var reentryAttempted = false;

        // Progress handler that re-enters CheckWifiFirmwareStatusAsync
        // exactly once (on the first event) — mirrors a UI consumer
        // that might want to refresh status display when an update
        // transitions state.
        var progress = new SyncProgress<FirmwareUpdateProgress>(_ =>
        {
            if (reentryAttempted)
            {
                return;
            }
            reentryAttempted = true;
            reentrantStatus = service.CheckWifiFirmwareStatusAsync(device).GetAwaiter().GetResult();
        });

        // Hard timeout via WaitAsync so a regression of the deadlock fails
        // fast instead of stalling the whole xunit run waiting for the
        // global per-test budget. The fast-options + ms-scale delays mean
        // the happy path completes in well under a second; 30s is plenty
        // of margin under heavy CI load while still being a clear "stuck".
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress)
                .WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!reentryAttempted)
        {
            throw new Xunit.Sdk.XunitException(
                "Test setup failed before reentrancy was attempted.");
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                "UpdateWifiModuleAsync did not complete within 30s — the "
                + "AsyncLocal re-entrancy guard likely regressed and the "
                + "inner CheckWifiFirmwareStatusAsync call is deadlocked.");
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.True(reentryAttempted, "Progress callback never fired — test setup wrong.");
        Assert.NotNull(reentrantStatus);
        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenLanChipInfoFailsTransiently_RetriesUntilSuccess()
    {
        // Closes #144: post-PIC32-reboot the WiFi subsystem can lag the
        // application by a few seconds, so the first chip-info query
        // transiently fails. Without retry, the WiFi version decision
        // would short-circuit and trigger an unnecessary multi-minute
        // reflash. The retry budget covers the startup window.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice(
            "COM14",
            chipInfo: new LanChipInfo
            {
                ChipId = 1234,
                FwVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            },
            transientFailuresBeforeSuccess: 2);

        var options = CreateFastOptions();
        options.LanChipInfoMaxAttempts = 3;
        options.LanChipInfoRetryDelay = TimeSpan.FromMilliseconds(5); // Keep test fast

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetLanChipInfoCallCount);
        Assert.True(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenLanChipInfoFailsAllAttempts_ReturnsChipInfoUnavailable()
    {
        // After exhausting LanChipInfoMaxAttempts the planning method must
        // fall through to ChipInfoUnavailable, NOT hang or surface the
        // raw exception. This preserves the "couldn't check, default to
        // running update" semantics for genuinely-broken devices.
        var device = new FakeLanChipInfoStreamingDevice(
            "COM15",
            chipInfo: new LanChipInfo
            {
                ChipId = 1234,
                FwVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            },
            transientFailuresBeforeSuccess: 99);

        var options = CreateFastOptions();
        options.LanChipInfoMaxAttempts = 3;
        options.LanChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetLanChipInfoCallCount);
        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_TotalTimeoutHit_ShortCircuitsToChipInfoUnavailable()
    {
        // Closes a Qodo follow-up on PR #199: per-attempt query timeouts
        // compound with retry delays, so a high MaxAttempts × non-trivial
        // per-attempt latency could block far beyond the configured retry
        // budget while holding _operationLock. The total-timeout cap caps
        // wall-clock time independent of attempt counts.
        //
        // The fake's per-attempt latency is the Task.Delay below; with
        // 200ms latency × 10 max attempts × 100ms retry delay, a naive
        // implementation would block ~2.9s. The 100ms total timeout
        // forces an early ChipInfoUnavailable return after the first
        // attempt's latency exceeds the budget.
        var device = new SlowFakeLanChipInfoStreamingDevice(
            "COM17",
            attemptLatency: TimeSpan.FromMilliseconds(200));

        var options = CreateFastOptions();
        options.LanChipInfoMaxAttempts = 10;
        options.LanChipInfoRetryDelay = TimeSpan.FromMilliseconds(100);
        options.LanChipInfoTotalTimeout = TimeSpan.FromMilliseconds(100);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var status = await service.CheckWifiFirmwareStatusAsync(device);
        stopwatch.Stop();

        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, status.Reason);
        // Should bail well before attempting all 10 × (200ms + 100ms) = 3s.
        // Allowing 1500ms for CI variance / xunit overhead.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"Probe took {stopwatch.ElapsedMilliseconds}ms, expected <1500ms with TotalTimeout=100ms.");
    }

    private sealed class SlowFakeLanChipInfoStreamingDevice : IStreamingDevice, ILanChipInfoProvider
    {
        private readonly TimeSpan _attemptLatency;
        public SlowFakeLanChipInfoStreamingDevice(string name, TimeSpan attemptLatency)
        {
            Name = name;
            _attemptLatency = attemptLatency;
            IsConnected = true;
        }
        public string Name { get; }
        public IPAddress? IpAddress => null;
        public bool IsConnected { get; private set; }
        public ConnectionStatus Status => ConnectionStatus.Connected;
        public int StreamingFrequency { get; set; }
        public bool IsStreaming { get; private set; }
        public event EventHandler<DeviceStatusEventArgs>? StatusChanged { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;
        public void Send<T>(IOutboundMessage<T> message) { }
        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IChannel channel, bool value) { }
        public void SetAnalogOutput(int channelNumber, double voltage) { }
        public void Reboot() => IsConnected = false;
        public async Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_attemptLatency, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_FirstAttemptSucceeds_DoesNotRetry()
    {
        // Steady-state path: no retry overhead when the first call works.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeLanChipInfoStreamingDevice(
            "COM16",
            chipInfo: new LanChipInfo
            {
                ChipId = 1234,
                FwVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        options.LanChipInfoMaxAttempts = 3;
        options.LanChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(1, device.GetLanChipInfoCallCount);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private static FirmwareUpdateServiceOptions CreateFastOptions()
    {
        return new FirmwareUpdateServiceOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            PreparingDeviceTimeout = TimeSpan.FromSeconds(2),
            WaitingForBootloaderTimeout = TimeSpan.FromSeconds(2),
            ConnectingTimeout = TimeSpan.FromSeconds(2),
            ErasingFlashTimeout = TimeSpan.FromSeconds(2),
            ProgrammingTimeout = TimeSpan.FromSeconds(5),
            VerifyingTimeout = TimeSpan.FromSeconds(2),
            JumpingToApplicationTimeout = TimeSpan.FromSeconds(2),
            BootloaderResponseTimeout = TimeSpan.FromMilliseconds(250),
            PostForceBootDelay = TimeSpan.FromMilliseconds(5),
            PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5),
            PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5),
            HidConnectRetryDelay = TimeSpan.FromMilliseconds(5),
            FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5),
            WifiProcessTimeout = TimeSpan.FromSeconds(2),
            // Collapse the WiFi-flash lifecycle waits for unit tests: the fake transport
            // releases its port synchronously and the fake process runner needs no
            // prompt/bridge-init window, so these delays are pure latency here.
            PostLanDisconnectPortReleaseDelay = TimeSpan.Zero,
            WincBootPromptResponseDelay = TimeSpan.Zero,
            WifiFlashRetryDelay = TimeSpan.FromMilliseconds(5),
            // Disable the macOS USB CDC stale-handle settling delay for unit
            // tests — FakeStreamingDevice doesn't reproduce the re-enum race,
            // so the dance is pure latency that would blow the fast budgets.
            PostReconnectStaleHandleDelay = TimeSpan.Zero
        };
    }

    private static string CreateTempFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "test");
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "daqifi-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeStreamingDevice : IStreamingDevice
    {
        private ConnectionStatus _status = ConnectionStatus.Connected;

        public FakeStreamingDevice(string name)
        {
            Name = name;
            IsConnected = true;
        }

        public string Name { get; }
        public IPAddress? IpAddress => null;
        public bool IsConnected { get; private set; }
        public ConnectionStatus Status => _status;
        public int StreamingFrequency { get; set; }
        public bool IsStreaming { get; private set; }

        public int ConnectAttempts { get; private set; }
        public int DisconnectCalls { get; private set; }

        public int ConnectFailuresBeforeSuccess { get; set; }
        public List<string> SentCommands { get; } = [];

        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public void Connect()
        {
            ConnectAttempts++;
            if (ConnectFailuresBeforeSuccess > 0)
            {
                ConnectFailuresBeforeSuccess--;
                throw new IOException("Simulated serial reconnect failure.");
            }

            IsConnected = true;
            _status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
        }

        public void Disconnect()
        {
            DisconnectCalls++;
            IsConnected = false;
            _status = ConnectionStatus.Disconnected;
            StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
        }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> textMessage)
            {
                SentCommands.Add(textMessage.Data);
            }
        }

        public void StartStreaming()
        {
            IsStreaming = true;
        }

        public void StopStreaming()
        {
            IsStreaming = false;
        }

        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IChannel channel, bool value) { }
        public void SetAnalogOutput(int channelNumber, double voltage) { }
        public void Reboot() => Disconnect();
    }

    private sealed class FakeLanChipInfoStreamingDevice : IStreamingDevice, ILanChipInfoProvider
    {
        private readonly LanChipInfo? _chipInfo;
        private ConnectionStatus _status = ConnectionStatus.Connected;
        private int _remainingTransientFailures;

        public FakeLanChipInfoStreamingDevice(string name, LanChipInfo? chipInfo, int transientFailuresBeforeSuccess = 0)
        {
            Name = name;
            _chipInfo = chipInfo;
            _remainingTransientFailures = transientFailuresBeforeSuccess;
            IsConnected = true;
        }

        public int GetLanChipInfoCallCount { get; private set; }

        public string Name { get; }
        public IPAddress? IpAddress => null;
        public bool IsConnected { get; private set; }
        public ConnectionStatus Status => _status;
        public int StreamingFrequency { get; set; }
        public bool IsStreaming { get; private set; }

        public List<string> SentCommands { get; } = [];

        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public void Connect()
        {
            IsConnected = true;
            _status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
        }

        public void Disconnect()
        {
            IsConnected = false;
            _status = ConnectionStatus.Disconnected;
            StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
        }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> textMessage)
            {
                SentCommands.Add(textMessage.Data);
            }
        }

        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IChannel channel, bool value) { }
        public void SetAnalogOutput(int channelNumber, double voltage) { }
        public void Reboot() => Disconnect();

        public Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            GetLanChipInfoCallCount++;
            if (_remainingTransientFailures > 0)
            {
                _remainingTransientFailures--;
                // Faulted task (not sync throw) more accurately simulates how
                // a real async method surfaces failure — caller's await sees a
                // genuinely-async exception path.
                return Task.FromException<LanChipInfo?>(
                    new InvalidOperationException("Simulated transient post-reboot failure."));
            }
            return Task.FromResult(_chipInfo);
        }
    }

    private sealed class FakeHidTransport : IHidTransport
    {
        private readonly Queue<byte[]> _readQueue = new();

        public bool IsConnected { get; private set; }
        public int? VendorId { get; private set; }
        public int? ProductId { get; private set; }
        public string? SerialNumber { get; private set; }
        public string? DevicePath { get; private set; }
        public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public List<byte[]> Writes { get; } = [];
        public int ConnectAttempts { get; private set; }
        public int DisconnectCalls { get; private set; }

        public void EnqueueRead(byte[] response)
        {
            _readQueue.Enqueue(response);
        }

        public Task ConnectAsync(int vendorId, int productId, string? serialNumber = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectAttempts++;
            IsConnected = true;
            VendorId = vendorId;
            ProductId = productId;
            SerialNumber = serialNumber;
            DevicePath = "fake-path";
            return Task.CompletedTask;
        }

        public void Connect(int vendorId, int productId, string? serialNumber = null)
        {
            ConnectAsync(vendorId, productId, serialNumber).GetAwaiter().GetResult();
        }

        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected.");
            }

            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public void Write(byte[] data)
        {
            WriteAsync(data).GetAwaiter().GetResult();
        }

        public Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected.");
            }

            if (_readQueue.Count == 0)
            {
                throw new InvalidOperationException("No queued HID response available.");
            }

            return Task.FromResult(_readQueue.Dequeue());
        }

        public byte[] Read(TimeSpan? timeout = null)
        {
            return ReadAsync(timeout).GetAwaiter().GetResult();
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            VendorId = null;
            ProductId = null;
            SerialNumber = null;
            DevicePath = null;
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeHidDeviceEnumerator : IHidDeviceEnumerator
    {
        private readonly Queue<IReadOnlyList<HidDeviceInfo>> _responses;
        private readonly IReadOnlyList<HidDeviceInfo> _fallback;

        public FakeHidDeviceEnumerator(
            IReadOnlyList<IReadOnlyList<HidDeviceInfo>> responses,
            IReadOnlyList<HidDeviceInfo>? fallback = null)
        {
            _responses = new Queue<IReadOnlyList<HidDeviceInfo>>(responses);
            _fallback = fallback ?? Array.Empty<HidDeviceInfo>();
        }

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
            int? vendorId = null,
            int? productId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_responses.Count > 0)
            {
                return Task.FromResult(_responses.Dequeue());
            }

            return Task.FromResult(_fallback);
        }
    }

    private sealed class ThrowingHidDeviceEnumerator : IHidDeviceEnumerator
    {
        private readonly Exception _exception;

        public ThrowingHidDeviceEnumerator(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
            int? vendorId = null,
            int? productId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IReadOnlyList<HidDeviceInfo>>(_exception);
        }
    }

    private sealed class FakeBootloaderProtocol : IBootloaderProtocol
    {
        private readonly IReadOnlyList<byte[]> _hexRecords;
        private readonly IReadOnlyList<FlashCrcRegion> _crcRegions;

        public FakeBootloaderProtocol(
            IReadOnlyList<byte[]> hexRecords,
            IReadOnlyList<FlashCrcRegion>? crcRegions = null)
        {
            _hexRecords = hexRecords;
            _crcRegions = crcRegions ?? Array.Empty<FlashCrcRegion>();
        }

        public byte[] CreateRequestVersionMessage() => [0x11];
        public byte[] CreateEraseFlashMessage() => [0x22];
        public byte[] CreateProgramFlashMessage(byte[] hexRecord) => [0x33, .. hexRecord];
        public byte[] CreateReadCrcMessage(uint address, uint length) =>
            [0x44, .. BitConverter.GetBytes(address), .. BitConverter.GetBytes(length)];
        public byte[] CreateJumpToApplicationMessage() => [0x55];

        public string DecodeVersionResponse(byte[] data)
        {
            if (data.Length == 0 || data[0] == 0xEE)
            {
                return "Error";
            }

            return "1.0";
        }

        public bool DecodeProgramFlashResponse(byte[] data)
        {
            return data.Length >= 2 && data[0] == 0x01 && data[1] == 0x03;
        }

        public bool DecodeEraseFlashResponse(byte[] data)
        {
            return data.Length >= 2 && data[0] == 0x01 && data[1] == 0x02;
        }

        // The enqueued READ_CRC HID response carries the device-reported CRC in
        // its first two bytes (little-endian), so a test drives match/mismatch
        // by enqueuing specific bytes.
        public ushort DecodeReadCrcResponse(byte[] data)
        {
            if (data.Length < 2)
            {
                throw new InvalidDataException("Fake READ_CRC response was too short.");
            }

            return (ushort)(data[0] | (data[1] << 8));
        }

        public IReadOnlyList<FlashCrcRegion> ComputeCrcRegions(string[] hexFileLines) => _crcRegions;

        public List<byte[]> ParseHexFile(string[] hexFileLines)
        {
            return _hexRecords.Select(record => record.ToArray()).ToList();
        }
    }

    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        public ExternalProcessResult NextResult { get; set; } = new(0, false, TimeSpan.Zero, [], []);

        /// <summary>
        /// Optional per-attempt results. When non-empty each <see cref="RunAsync"/> call dequeues
        /// the next result, letting tests model the WINC flash retry path (transient failure →
        /// success). Once the sequence is drained, subsequent calls return <see cref="NextResult"/>
        /// — kept deliberately distinct from the scripted results so a test asserting
        /// <see cref="RunCount"/> can still catch unexpected extra attempts.
        /// </summary>
        public Queue<ExternalProcessResult> ResultSequence { get; } = new();

        public ExternalProcessRequest? LastRequest { get; private set; }
        public int RunCount { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            RunCount++;

            var result = ResultSequence.Count > 0 ? ResultSequence.Dequeue() : NextResult;

            foreach (var line in result.StandardOutputLines)
            {
                request.OnStandardOutputLine?.Invoke(line);
                request.StandardInputResponseFactory?.Invoke(line);
            }

            foreach (var line in result.StandardErrorLines)
            {
                request.OnStandardErrorLine?.Invoke(line);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class FakeFirmwareDownloadService : IFirmwareDownloadService
    {
        public FirmwareReleaseInfo? LatestWifiRelease { get; set; }

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(bool includePreRelease = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<FirmwareReleaseInfo?>(null);
        }

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(string deviceVersionString, bool includePreRelease = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new FirmwareUpdateCheckResult
            {
                UpdateAvailable = false
            });
        }

        public Task<string?> DownloadLatestFirmwareAsync(string destinationDirectory, bool includePreRelease = false, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> DownloadFirmwareByTagAsync(string tagName, string destinationDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(string destinationDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<(string ExtractedPath, string Version)?>(null);
        }

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LatestWifiRelease);
        }

        public void InvalidateCache()
        {
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly ICollection<T> _items;

        public CapturingProgress(ICollection<T> items)
        {
            _items = items;
        }

        public void Report(T value)
        {
            _items.Add(value);
        }
    }
}
