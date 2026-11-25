using System.IO;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

public class FirmwareUpdateServiceTests
{
    [Fact]
    public void Constructor_WithoutLogger_UsesNullLogger_DoesNotThrow()
    {
        // #340: the service is usable without wiring a logger (falls back to NullLogger).
        // The only construction in the suite that omits the logger, so it is what keeps the
        // parameter optional.
        var ex = Record.Exception(() => new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner()));

        Assert.Null(ex);
    }

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
            await service.UpdateFirmwareAsync(device, hexPath, progress: progress);
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
                FirmwareUpdateState.JumpingToApplication,
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteWithRetryAsync_WithNonPositiveMaxAttempts_ThrowsAndNeverRunsTheAction(int maxAttempts)
    {
        // The retry helper is shared plumbing for flash-critical steps (erase, program,
        // verify). A caller asking for zero attempts must fail loudly rather than return
        // a silent success that skipped the operation entirely.
        var context = new FirmwareUpdateContext(
            new object(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FirmwareUpdateServiceOptions());

        var actionRan = false;

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => context.ExecuteWithRetryAsync(
                "erase flash",
                maxAttempts,
                TimeSpan.Zero,
                _ =>
                {
                    actionRan = true;
                    return Task.CompletedTask;
                },
                _ => true,
                CancellationToken.None));

        Assert.Equal("maximumAttempts", ex.ParamName);
        Assert.False(actionRan);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithSingleAttempt_RunsTheActionExactlyOnce()
    {
        // Guards the boundary: 1 is valid and must not be caught by the new check.
        var context = new FirmwareUpdateContext(
            new object(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FirmwareUpdateServiceOptions());

        var runCount = 0;

        await context.ExecuteWithRetryAsync(
            "erase flash",
            1,
            TimeSpan.Zero,
            _ =>
            {
                runCount++;
                return Task.CompletedTask;
            },
            _ => true,
            CancellationToken.None);

        Assert.Equal(1, runCount);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_RaisesStateChangedWithTheServiceAsSender()
    {
        // The state machine lives in an internal collaborator, so the event's
        // sender is forwarded rather than being an implicit `this`. Subscribers
        // that key off sender (e.g. a UI tracking several services) must keep
        // seeing the public service instance.
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

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol(
                [[0xA1, 0x01], [0xA1, 0x02]],
                crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]),
            enumerator,
            CreateFastOptions());

        var senders = new List<object?>();
        service.StateChanged += (sender, _) => senders.Add(sender);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.NotEmpty(senders);
        Assert.All(senders, sender => Assert.Same(service, sender));
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
    public async Task UpdateFirmwareAsync_WithTargetDevicePath_FlashesThatExactBootloaderByPath()
    {
        // Multi-device: several identical bootloaders (same VID/PID, no serial) are enumerated at once.
        // targetDevicePath must select and connect to that EXACT device by path — not VID/PID first-match.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0xCD, 0xAB]); // READ_CRC response → 0xABCD (match)

        var enumerator = new FakeHidDeviceEnumerator([
            [
                new HidDeviceInfo(0x04D8, 0x003C, "path-1", null, "DAQiFi Bootloader"),
                new HidDeviceInfo(0x04D8, 0x003C, "path-2", null, "DAQiFi Bootloader")
            ]
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

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath, new FirmwareUpdateTarget { DevicePath = "path-2" });
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal("path-2", hidTransport.LastConnectByPath);
        Assert.Equal(1, hidTransport.ConnectByPathAttempts);
        Assert.Equal(0, hidTransport.ConnectAttempts); // did NOT fall back to VID/PID first-match
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithTargetLocationKey_FlashesThatExactBootloaderByLocation()
    {
        // Same multi-device scenario as the targetDevicePath test above, but the caller only
        // knows the target's USB physical-location key (resolved before the reboot into
        // bootloader mode, when the device was still enumerable by its old identity) — not its
        // future bootloader device path. WaitForBootloaderDeviceAsync must resolve each
        // candidate's location via the injected provider and match on that.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0xCD, 0xAB]); // READ_CRC response → 0xABCD (match)

        var enumerator = new FakeHidDeviceEnumerator([
            [
                new HidDeviceInfo(0x04D8, 0x003C, "path-1", null, "DAQiFi Bootloader"),
                new HidDeviceInfo(0x04D8, 0x003C, "path-2", null, "DAQiFi Bootloader")
            ]
        ]);

        var locationProvider = new FakeUsbLocationProvider(new Dictionary<string, string>
        {
            ["path-1"] = "Port_#0001.Hub_#0001",
            ["path-2"] = "Port_#0002.Hub_#0001"
        });

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
            CreateFastOptions(),
            locationProvider);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(
                device, hexPath, new FirmwareUpdateTarget { LocationKey = "Port_#0002.Hub_#0001" });
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal("path-2", hidTransport.LastConnectByPath);
        Assert.Equal(1, hidTransport.ConnectByPathAttempts);
        Assert.Equal(0, hidTransport.ConnectAttempts); // did NOT fall back to VID/PID first-match
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithTargetDevicePathAndTargetLocationKey_DevicePathTakesPriority()
    {
        // When both are supplied, targetDevicePath must win — it's an exact identifier while
        // targetLocationKey depends on a resolvable provider.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0xCD, 0xAB]); // READ_CRC response → 0xABCD (match)

        var enumerator = new FakeHidDeviceEnumerator([
            [
                new HidDeviceInfo(0x04D8, 0x003C, "path-1", null, "DAQiFi Bootloader"),
                new HidDeviceInfo(0x04D8, 0x003C, "path-2", null, "DAQiFi Bootloader")
            ]
        ]);

        // Deliberately mismatched: the location key would resolve to path-2, but the explicit
        // device path (path-1) must win.
        var locationProvider = new FakeUsbLocationProvider(new Dictionary<string, string>
        {
            ["path-1"] = "Port_#0001.Hub_#0001",
            ["path-2"] = "Port_#0002.Hub_#0001"
        });

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
            CreateFastOptions(),
            locationProvider);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(
                device,
                hexPath,
                new FirmwareUpdateTarget { DevicePath = "path-1", LocationKey = "Port_#0002.Hub_#0001" });
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal("path-1", hidTransport.LastConnectByPath);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithTargetLocationKey_ResolvesEachDevicePathAtMostOncePerPoll()
    {
        // Regression test: WaitForBootloaderDeviceAsync used to call GetLocationKey(...) inside
        // its FirstOrDefault predicate on every poll iteration, re-resolving the SAME device
        // path's (WMI, on the real Windows provider) location repeatedly while polling. A device
        // path's physical location can't change while it stays enumerated, so a per-call cache
        // should mean each unique path is resolved at most once across the whole wait, no matter
        // how many poll iterations it takes to see a match. path-1 is enumerated on BOTH polls
        // below; path-2 (the match) only appears on the second.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0xCD, 0xAB]); // READ_CRC response → 0xABCD (match)

        var enumerator = new FakeHidDeviceEnumerator([
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", null, "DAQiFi Bootloader")],
            [
                new HidDeviceInfo(0x04D8, 0x003C, "path-1", null, "DAQiFi Bootloader"),
                new HidDeviceInfo(0x04D8, 0x003C, "path-2", null, "DAQiFi Bootloader")
            ]
        ]);

        var locationProvider = new FakeUsbLocationProvider(new Dictionary<string, string>
        {
            ["path-1"] = "Port_#0001.Hub_#0001",
            ["path-2"] = "Port_#0002.Hub_#0001"
        });

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
            CreateFastOptions(),
            locationProvider);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(
                device, hexPath, new FirmwareUpdateTarget { LocationKey = "Port_#0002.Hub_#0001" });
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal("path-2", hidTransport.LastConnectByPath);
        Assert.Equal(1, locationProvider.Requests.Count(p => p == "path-1"));
        Assert.Equal(1, locationProvider.Requests.Count(p => p == "path-2"));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithWhitespaceTargetLocationKey_ThrowsArgumentException()
    {
        // Same fast-fail contract as DevicePath: whitespace can never match a resolved
        // location key, so fail before polling until the WaitingForBootloader timeout.
        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var device = new FakeStreamingDevice("COM3");
        var hexPath = CreateTempFile();
        try
        {
            var failure = await Assert.ThrowsAsync<ArgumentException>(
                () => service.UpdateFirmwareAsync(
                    device, hexPath, new FirmwareUpdateTarget { LocationKey = "   " }));
            Assert.Equal("target", failure.ParamName);
        }
        finally
        {
            File.Delete(hexPath);
        }
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithWhitespaceTargetDevicePath_ThrowsArgumentException()
    {
        // A whitespace target can never match an enumerated path, so fail fast instead of polling
        // until the WaitingForBootloader timeout. (null target is allowed = untargeted.)
        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var device = new FakeStreamingDevice("COM3");
        var hexPath = CreateTempFile();
        try
        {
            var failure = await Assert.ThrowsAsync<ArgumentException>(
                () => service.UpdateFirmwareAsync(device, hexPath, new FirmwareUpdateTarget { DevicePath = "   " }));
            Assert.Equal("target", failure.ParamName);
        }
        finally
        {
            File.Delete(hexPath);
        }
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

        var stateEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => stateEvents.Add(args);
        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath, progress: progress));
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

        // #944: the re-erase left a clean bootloader, reported as a value on the exception and
        // the terminal event.
        AssertFlashCondition(Pic32FlashCondition.CleanBootloader, FirmwareUpdateState.Recovered, (exception, service, stateEvents));

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

        var stateEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => stateEvents.Add(args);
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

        // #944: a failed re-erase leaves the flash possibly half-flashed, reported as a value on
        // the exception and the terminal event.
        AssertFlashCondition(Pic32FlashCondition.PossiblyHalfFlashed, FirmwareUpdateState.Failed, (exception, service, stateEvents));

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
    public async Task UpdateFirmwareAsync_WhenBootloaderNeverAppears_FailsWithoutTouchingTheFlash()
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

        var stateEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => stateEvents.Add(args);

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
        // The diagnostics pass their own failure subject; the real update flow must keep the
        // wording it has always produced.
        Assert.StartsWith("Firmware update failed", exception.Message, StringComparison.Ordinal);

        var timeoutException = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("No matching HID bootloader device was enumerated", timeoutException.Message);
        Assert.Contains("VID=0x04D8", timeoutException.Message);
        Assert.Contains("PID=0x003C", timeoutException.Message);

        // #944: nothing was erased, and the exception and the terminal event both say so.
        AssertFlashCondition(Pic32FlashCondition.NotChanged, FirmwareUpdateState.Failed, (exception, service, stateEvents));
        Assert.DoesNotContain(stateEvents, e => e.CurrentState == FirmwareUpdateState.CleaningUp);
        Assert.DoesNotContain(stateEvents, e => e.CurrentState == FirmwareUpdateState.Recovered);
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
    public async Task UpdateFirmwareAsync_WhenBootloaderHealthCheckFails_SoftResetsAndCompletes()
    {
        // #298: a dirty HID bootloader handle (left behind by another program)
        // makes the initial version health check fail even though the device is
        // physically present. One JMP_TO_APP soft reset forces a clean
        // re-enumeration; the retried health check then succeeds and the update
        // proceeds normally.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0xEE]);       // initial version check -> "Error" (dirty handle)
        hidTransport.EnqueueRead([0x01, 0x10]); // version check after soft reset -> healthy
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x01, 0x10]); // verify version (no CRC regions configured)

        var bootloaderDevice = new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader");
        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [bootloaderDevice],
            [bootloaderDevice] // re-enumeration after the JMP_TO_APP soft reset
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
        Assert.Equal(
            [
                FirmwareUpdateState.PreparingDevice,
                FirmwareUpdateState.WaitingForBootloader,
                FirmwareUpdateState.Connecting,
                FirmwareUpdateState.ErasingFlash,
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Verifying,
                FirmwareUpdateState.JumpingToApplication,
                FirmwareUpdateState.Complete
            ],
            stateTransitions);

        // The JMP_TO_APP soft-reset message was written exactly once, before the
        // successful jump-to-application write at the end of the update.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x55));
        // The version request was sent twice: the failed initial check and the
        // successful retry after the soft reset.
        Assert.Equal(3, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x11));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenSoftResetRecoveryAlsoFails_FallsThroughToFailedWithGuidance()
    {
        // #298: when the JMP_TO_APP soft-reset recovery does not restore a
        // healthy bootloader session (health check fails again after the
        // reset), the update must fall through to today's Connecting-state
        // failure/guidance behavior rather than throwing an unrelated error.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0xEE]); // initial version check -> "Error"
        hidTransport.EnqueueRead([0xEE]); // version check after soft reset -> still "Error"

        var bootloaderDevice = new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader");
        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [bootloaderDevice],
            [bootloaderDevice] // re-enumeration after the JMP_TO_APP soft reset
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

        Assert.Equal(FirmwareUpdateState.Connecting, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.DoesNotContain(FirmwareUpdateState.CleaningUp, stateTransitions);
        Assert.DoesNotContain(FirmwareUpdateState.Recovered, stateTransitions);

        // The original (pre-recovery) failure surfaces to the caller, not a new
        // exception type introduced by the recovery attempt.
        var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("invalid version response", inner.Message);

        // The soft reset was still attempted before giving up.
        Assert.Contains(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x55);

        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("Bootloader was found but HID connection failed", exception.RecoveryGuidance);
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
        FirmwareUpdateStateChangedEventArgs? terminalEvent = null;
        service.StateChanged += (_, args) =>
        {
            stateTransitions.Add(args.CurrentState);
            terminalEvent = args;
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
        // #944: a cancel throws no FirmwareUpdateException, so the terminal event carries the
        // flash condition as a value.
        Assert.NotNull(terminalEvent);
        Assert.Equal(FirmwareUpdateState.Recovered, terminalEvent.CurrentState);
        Assert.Equal(Pic32FlashCondition.CleanBootloader, terminalEvent.FlashCondition);
        // Original erase + cleanup re-erase = two erase commands; no jump.
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        Assert.DoesNotContain(hidTransport.Writes, w => w.Length > 0 && w[0] == 0x55);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCanceledBeforeFlash_SurfacesCanceledOperationToObservers()
    {
        // The rethrown OperationCanceledException carries no context, so the
        // terminal StateChanged/progress text is the only channel telling a UI
        // "the user canceled" apart from a device failure.
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

        var stateEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => stateEvents.Add(args);

        // Cancel on reaching WaitingForBootloader rather than on a timer: a timer can fire before
        // the update even starts (ending in Idle) on a loaded CI runner.
        using var cts = new CancellationTokenSource();
        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.WaitingForBootloader)
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

        // Not cleanup-eligible: nothing to re-erase before the flash is touched, so it ends in Failed.
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        var terminal = stateEvents[^1];
        Assert.Equal(FirmwareUpdateState.Failed, terminal.CurrentState);
        Assert.Equal("PIC32 firmware update canceled.", terminal.Operation);
        // #944: nothing was erased, and the event is the only place a cancel can say so.
        Assert.Equal(Pic32FlashCondition.NotChanged, terminal.FlashCondition);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenTransportDropsMidFlash_SurfacesHalfFlashedWarningToObservers()
    {
        // A flash-touching failure where the HID transport itself dropped is
        // cleanup-eligible but cleanup CANNOT run. Observers must still get the
        // half-flashed warning in the terminal event (not a stale in-flight
        // message), because on this path it may be their only signal.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.DropWhenReadQueueEmpty();  // program ack 2 → transport drops

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

        var stateEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => stateEvents.Add(args);

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
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        // No CleaningUp detour — the transport is gone, so no re-erase was possible.
        Assert.DoesNotContain(stateEvents, e => e.CurrentState == FirmwareUpdateState.CleaningUp);
        Assert.Equal(1, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        // Both the exception guidance AND the terminal event text warn half-flashed.
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("half-flashed", exception.RecoveryGuidance);
        var terminal = stateEvents[^1];
        Assert.Equal(FirmwareUpdateState.Failed, terminal.CurrentState);
        Assert.Contains("half-flashed", terminal.Operation);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenObserverThrowsOnCleaningUp_EndsInFailedNotWedged()
    {
        // A throwing StateChanged subscriber at the CleaningUp transition must
        // not strand the machine in the non-terminal CleaningUp state (which has
        // no reset path) or replace the original failure exception.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x34, 0x12]); // READ_CRC → mismatch

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

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

        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.CleaningUp)
            {
                throw new InvalidOperationException("Subscriber failure on CleaningUp.");
            }
        };

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

        // Terminal Failed (resettable), original failure context preserved, and
        // guidance reflects that the cleanup did not complete.
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.Verifying, exception.FailedState);
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("half-flashed", exception.RecoveryGuidance);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenObserverThrowsOnRecovered_KeepsRecoveredOutcome()
    {
        // If the re-erase itself succeeded, a consumer callback throwing on the
        // Recovered notification must not flip the outcome to half-flashed (and
        // Recovered → Failed is not a legal transition anyway).
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x34, 0x12]); // READ_CRC → mismatch
        hidTransport.EnqueueRead([0x01, 0x02]); // cleanup re-erase ack

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [[0xA1, 0x01], [0xA1, 0x02]],
            crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]);

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

        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.Recovered)
            {
                throw new InvalidOperationException("Subscriber failure on Recovered.");
            }
        };

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

        // The device WAS re-erased: outcome stays Recovered end-to-end.
        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.Verifying, exception.FailedState);
        Assert.Equal(2, hidTransport.Writes.Count(w => w.Length > 0 && w[0] == 0x22));
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Contains("clean bootloader state", exception.RecoveryGuidance);
    }

    // ----- A consumer callback throwing on Failed must not replace the real failure -----

    private const string FailedObserverMessage = "Observer failure on Failed.";

    private static void ThrowFromStateChangedOnFailed(FirmwareUpdateService service)
    {
        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.Failed)
            {
                throw new InvalidOperationException(FailedObserverMessage);
            }
        };
    }

    /// <summary>
    /// A progress sink that records every report and, when <paramref name="throwOnFailed"/> is
    /// set, then throws on the Failed one.
    /// </summary>
    private static SyncProgress<FirmwareUpdateProgress> RecordingProgress(
        List<FirmwareUpdateProgress> reports,
        bool throwOnFailed)
    {
        return new SyncProgress<FirmwareUpdateProgress>(p =>
        {
            reports.Add(p);
            if (throwOnFailed && p.State == FirmwareUpdateState.Failed)
            {
                throw new InvalidOperationException(FailedObserverMessage);
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateFirmwareAsync_WhenObserverThrowsOnFailed_KeepsFirmwareUpdateException(
        bool cleanupEraseRuns,
        bool fromProgressSink)
    {
        // Both Failed endings of the PIC32 cleanup: the HID link is gone so no re-erase can run,
        // or the re-erase runs and fails. Either way the caller must still get the
        // FirmwareUpdateException with its half-flashed verdict, not the observer's exception.
        var reports = new List<FirmwareUpdateProgress>();
        var progress = RecordingProgress(reports, throwOnFailed: fromProgressSink);
        Action<FirmwareUpdateService>? subscriber = fromProgressSink ? null : ThrowFromStateChangedOnFailed;

        var run = cleanupEraseRuns
            ? await RunFailingPic32UpdateAsync(
                hidReads: [VersionAck, EraseAck, ProgramAck, ProgramAck, CrcMismatch, InvalidAck],
                verifyCrc: true,
                configureService: subscriber,
                progress: progress)
            : await RunFailingPic32UpdateAsync(
                hidReads: [VersionAck, EraseAck, ProgramAck],
                dropTransportWhenReadsRunOut: true,
                configureService: subscriber,
                progress: progress);

        Assert.Equal(
            cleanupEraseRuns ? FirmwareUpdateState.Verifying : FirmwareUpdateState.Programming,
            run.Exception.FailedState);
        Assert.NotNull(run.Exception.RecoveryGuidance);
        Assert.Contains("half-flashed", run.Exception.RecoveryGuidance);
        AssertFlashCondition(Pic32FlashCondition.PossiblyHalfFlashed, FirmwareUpdateState.Failed, run);
        // The terminal progress report still goes out even when the state subscriber threw first.
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateFirmwareAsync_WhenCanceledAndObserverThrowsOnFailed_KeepsOperationCanceledException(
        bool fromProgressSink)
    {
        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromSeconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>()),
            options);

        using var cts = new CancellationTokenSource();
        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.WaitingForBootloader)
            {
                cts.Cancel();
            }
        };
        if (!fromProgressSink)
        {
            ThrowFromStateChangedOnFailed(service);
        }

        var reports = new List<FirmwareUpdateProgress>();
        var hexPath = CreateTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateFirmwareAsync(
                    new FakeStreamingDevice("COM3"),
                    hexPath,
                    progress: RecordingProgress(reports, throwOnFailed: fromProgressSink),
                    cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Failed);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCanceledMidFlashAndFirstSubscriberThrowsOnFailed_LaterSubscriberStillGetsFlashCondition()
    {
        // A cancel throws no FirmwareUpdateException, so the terminal event's flash condition is
        // the only typed signal that the board may be half-flashed. A subscriber that throws on
        // it must not keep the event from the subscribers added after it.
        var hidTransport = new FakeHidTransport();
        foreach (var read in new[] { VersionAck, EraseAck, InvalidAck })
        {
            hidTransport.EnqueueRead(read); // version, erase, cleanup re-erase rejected
        }

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1;

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            options);

        using var cts = new CancellationTokenSource();
        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.Programming)
            {
                cts.Cancel();
            }

            if (args.CurrentState == FirmwareUpdateState.Failed)
            {
                throw new InvalidOperationException(FailedObserverMessage);
            }
        };
        var laterEvents = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => laterEvents.Add(args);

        var hexPath = CreateTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateFirmwareAsync(
                    new FakeStreamingDevice("COM3"), hexPath, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Contains(laterEvents, e => e.CurrentState == FirmwareUpdateState.CleaningUp);
        var failed = Assert.Single(laterEvents, e => e.CurrentState == FirmwareUpdateState.Failed);
        Assert.Equal(Pic32FlashCondition.PossiblyHalfFlashed, failed.FlashCondition);
    }

    /// <summary>
    /// A logger that throws only on the log line of a transition into <paramref name="state"/> —
    /// a provider that misbehaves on exactly the terminal line.
    /// </summary>
    private sealed class ThrowOnTransitionLogger(FirmwareUpdateState state) : ILogger<FirmwareUpdateService>
    {
        public IDisposable? BeginScope<TState>(TState scopeState) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState logState,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(logState, exception);
            if (message.StartsWith("Firmware update state transition:", StringComparison.Ordinal) &&
                message.Contains($"-> {state} (", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Logger failure on the terminal transition.");
            }
        }
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenLoggerThrowsOnCompleteTransition_KeepsCompleteOutcome()
    {
        var hidTransport = new FakeHidTransport();
        foreach (var read in new[] { VersionAck, EraseAck, ProgramAck, ProgramAck, VersionAck })
        {
            hidTransport.EnqueueRead(read);
        }

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            new ThrowOnTransitionLogger(FirmwareUpdateState.Complete),
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            CreateFastOptions());

        var events = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => events.Add(args.CurrentState);
        var reports = new List<FirmwareUpdateProgress>();

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(
                new FakeStreamingDevice("COM3"), hexPath, progress: RecordingProgress(reports, throwOnFailed: false));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.Complete, events[^1]);
        Assert.DoesNotContain(FirmwareUpdateState.Failed, events);
        var complete = Assert.Single(reports, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, complete.PercentComplete);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenLoggerThrowsOnRecoveredTransition_KeepsRecoveredOutcome()
    {
        var hidTransport = new FakeHidTransport();
        foreach (var read in new[] { VersionAck, EraseAck, ProgramAck, ProgramAck, CrcMismatch, EraseAck })
        {
            hidTransport.EnqueueRead(read);
        }

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1;

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            new ThrowOnTransitionLogger(FirmwareUpdateState.Recovered),
            new FakeBootloaderProtocol(
                [[0xA1, 0x01], [0xA1, 0x02]],
                crcRegions: [new FlashCrcRegion(0x9D000000, 256, 0xABCD)]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            options);

        var events = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => events.Add(args);
        var reports = new List<FirmwareUpdateProgress>();

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(
                    new FakeStreamingDevice("COM3"), hexPath, progress: RecordingProgress(reports, throwOnFailed: false)));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Verifying, exception.FailedState);
        Assert.Equal(Pic32FlashCondition.CleanBootloader, exception.FlashCondition);
        Assert.Equal(FirmwareUpdateState.Recovered, service.CurrentState);
        var recovered = Assert.Single(events, e => e.CurrentState == FirmwareUpdateState.Recovered);
        Assert.Equal(Pic32FlashCondition.CleanBootloader, recovered.FlashCondition);
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Recovered);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenLoggerThrowsOnFailedTransition_KeepsFirmwareUpdateException()
    {
        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner
            {
                NextResult = new ExternalProcessResult(
                    1,
                    timedOut: false,
                    TimeSpan.FromMilliseconds(10),
                    ["some output"],
                    ["fatal"])
            },
            new ThrowOnTransitionLogger(FirmwareUpdateState.Failed),
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var events = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => events.Add(args);
        var reports = new List<FirmwareUpdateProgress>();

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(
                    new FakeStreamingDevice("COM8"),
                    firmwareDir,
                    progress: RecordingProgress(reports, throwOnFailed: false)));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        var failed = Assert.Single(events, e => e.CurrentState == FirmwareUpdateState.Failed);
        Assert.Equal(Pic32FlashCondition.NotChanged, failed.FlashCondition);
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWifiModuleAsync_WhenObserverThrowsOnFailed_KeepsFirmwareUpdateException(
        bool fromProgressSink)
    {
        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner
            {
                NextResult = new ExternalProcessResult(
                    1,
                    timedOut: false,
                    TimeSpan.FromMilliseconds(10),
                    ["some output"],
                    ["fatal"])
            },
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        if (!fromProgressSink)
        {
            ThrowFromStateChangedOnFailed(service);
        }

        var reports = new List<FirmwareUpdateProgress>();
        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(
                    new FakeStreamingDevice("COM8"),
                    firmwareDir,
                    progress: RecordingProgress(reports, throwOnFailed: fromProgressSink)));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
        Assert.Equal(Pic32FlashCondition.NotChanged, exception.FlashCondition);
        Assert.NotNull(exception.RecoveryGuidance);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWifiModuleAsync_WhenCanceledAndObserverThrowsOnFailed_KeepsOperationCanceledException(
        bool fromProgressSink)
    {
        var device = new FakeStreamingDevice("COM8");
        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromSeconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        if (!fromProgressSink)
        {
            ThrowFromStateChangedOnFailed(service);
        }

        // Cancel as the update-mode request goes out, so the cancel always lands in the pause
        // before the apply, however slow the runner is.
        using var cts = new CancellationTokenSource();
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:COMMUnicate:LAN:FWUpdate")
            {
                cts.Cancel();
            }
        };

        var reports = new List<FirmwareUpdateProgress>();
        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(
                    device,
                    firmwareDir,
                    progress: RecordingProgress(reports, throwOnFailed: fromProgressSink),
                    cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Single(reports, p => p.State == FirmwareUpdateState.Failed);
    }

    // ----- #944: the condition of the PIC32 flash is a value, not message text -----

    private static readonly byte[] VersionAck = [0x01, 0x10];
    private static readonly byte[] EraseAck = [0x01, 0x02];
    private static readonly byte[] ProgramAck = [0x01, 0x03];
    private static readonly byte[] CrcMismatch = [0x34, 0x12];
    private static readonly byte[] InvalidAck = [0x00];

    /// <summary>
    /// Runs a PIC32 update that is expected to fail and returns the thrown exception, the
    /// service, and every StateChanged event it raised.
    /// </summary>
    private static async Task<(FirmwareUpdateException Exception, FirmwareUpdateService Service, List<FirmwareUpdateStateChangedEventArgs> Events)>
        RunFailingPic32UpdateAsync(
            byte[][] hidReads,
            bool bootloaderEnumerates = true,
            bool dropTransportWhenReadsRunOut = false,
            bool verifyCrc = false,
            Action<FirmwareUpdateServiceOptions>? configure = null,
            Action<FirmwareUpdateService>? configureService = null,
            IProgress<FirmwareUpdateProgress>? progress = null)
    {
        var hidTransport = new FakeHidTransport();
        foreach (var read in hidReads)
        {
            hidTransport.EnqueueRead(read);
        }

        if (dropTransportWhenReadsRunOut)
        {
            hidTransport.DropWhenReadQueueEmpty();
        }

        var enumerator = bootloaderEnumerates
            ? new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ])
            : new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 1;
        configure?.Invoke(options);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol(
                [[0xA1, 0x01], [0xA1, 0x02]],
                crcRegions: verifyCrc ? [new FlashCrcRegion(0x9D000000, 256, 0xABCD)] : null),
            enumerator,
            options);

        var events = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => events.Add(args);
        configureService?.Invoke(service);

        var hexPath = CreateTempFile();
        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(new FakeStreamingDevice("COM3"), hexPath, progress: progress));
            return (exception, service, events);
        }
        finally
        {
            File.Delete(hexPath);
        }
    }

    /// <summary>
    /// The exception and the terminal event must report the same condition, and only the
    /// terminal event carries one.
    /// </summary>
    private static void AssertFlashCondition(
        Pic32FlashCondition expected,
        FirmwareUpdateState expectedTerminalState,
        (FirmwareUpdateException Exception, FirmwareUpdateService Service, List<FirmwareUpdateStateChangedEventArgs> Events) run)
    {
        Assert.Equal(expectedTerminalState, run.Service.CurrentState);
        Assert.Equal(expected, run.Exception.FlashCondition);

        var terminal = run.Events[^1];
        Assert.Equal(expectedTerminalState, terminal.CurrentState);
        Assert.Equal(expected, terminal.FlashCondition);
        Assert.All(run.Events.Take(run.Events.Count - 1), e => Assert.Null(e.FlashCondition));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenHidLinkIsLostMidFlash_ReportsFlashPossiblyHalfFlashed()
    {
        // Second program ack never arrives: the transport drops, so no cleanup erase can run.
        var run = await RunFailingPic32UpdateAsync(
            hidReads: [VersionAck, EraseAck, ProgramAck],
            dropTransportWhenReadsRunOut: true);

        Assert.Equal(FirmwareUpdateState.Programming, run.Exception.FailedState);
        Assert.DoesNotContain(run.Events, e => e.CurrentState == FirmwareUpdateState.CleaningUp);
        AssertFlashCondition(Pic32FlashCondition.PossiblyHalfFlashed, FirmwareUpdateState.Failed, run);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenJumpToAppFails_ReportsFlashProgrammedNotConfirmed()
    {
        // Everything is programmed; only the application never reports ready afterwards.
        var run = await RunFailingPic32UpdateAsync(
            hidReads: [VersionAck, EraseAck, ProgramAck, ProgramAck, VersionAck],
            configure: o =>
            {
                o.PostReconnectReadinessProbe = (_, _) => Task.FromResult(false);
                o.PostReconnectReadinessTimeout = TimeSpan.FromMilliseconds(150);
                o.PostReconnectReadinessRetryDelay = TimeSpan.FromMilliseconds(10);
            });

        Assert.Equal(FirmwareUpdateState.JumpingToApplication, run.Exception.FailedState);
        AssertFlashCondition(Pic32FlashCondition.ProgrammedNotConfirmed, FirmwareUpdateState.Failed, run);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenObserverThrowsOnComplete_KeepsCompleteOutcome()
    {
        // #944: the flash succeeded. A subscriber throwing on the Complete notification used to
        // send the updater into its failure path, which then died on the illegal
        // Complete -> Failed transition and handed the caller an InvalidOperationException.
        var hidTransport = new FakeHidTransport();
        foreach (var read in new[] { VersionAck, EraseAck, ProgramAck, ProgramAck, VersionAck })
        {
            hidTransport.EnqueueRead(read);
        }

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            CreateFastOptions());

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) =>
        {
            stateTransitions.Add(args.CurrentState);
            if (args.CurrentState == FirmwareUpdateState.Complete)
            {
                throw new InvalidOperationException("Subscriber failure on Complete.");
            }
        };

        var progressEvents = new List<FirmwareUpdateProgress>();
        var hexPath = CreateTempFile();
        try
        {
            // Must not throw at all — neither the subscriber's exception nor a transition error.
            await service.UpdateFirmwareAsync(
                new FakeStreamingDevice("COM3"), hexPath, progress: new CapturingProgress<FirmwareUpdateProgress>(progressEvents));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.Complete, stateTransitions[^1]);
        Assert.DoesNotContain(FirmwareUpdateState.Failed, stateTransitions);
        // The 100% report still goes out even though the state subscriber threw first.
        var complete = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, complete.PercentComplete);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenObserverThrowsOnComplete_KeepsCompleteOutcome()
    {
        // The WiFi flow had the same Complete -> Failed sequence as the PIC32 one (#944).
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

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) =>
        {
            stateTransitions.Add(args.CurrentState);
            if (args.CurrentState == FirmwareUpdateState.Complete)
            {
                throw new InvalidOperationException("Subscriber failure on Complete.");
            }
        };

        var progressEvents = new List<FirmwareUpdateProgress>();
        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");
        try
        {
            await service.UpdateWifiModuleAsync(
                device, firmwareDir, progress: new CapturingProgress<FirmwareUpdateProgress>(progressEvents));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(FirmwareUpdateState.Complete, stateTransitions[^1]);
        Assert.DoesNotContain(FirmwareUpdateState.Failed, stateTransitions);
        // A completed update never sends the failure-path bridge exit a second time.
        Assert.Equal(1, device.SentCommands.Count(c => c == "SYSTem:USB:SetTransparentMode 0"));
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.Complete && p.PercentComplete == 100);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenAlreadyUpToDateAndObserverThrowsOnComplete_KeepsCompleteOutcome()
    {
        // The up-to-date shortcut goes Idle -> Complete inside the same try block, so it had the
        // same exposure.
        var downloadService = new FakeFirmwareDownloadService
        {
            LatestWifiRelease = new FirmwareReleaseInfo
            {
                Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
                TagName = "19.5.4",
                IsPreRelease = false
            }
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM9", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
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

        service.StateChanged += (_, args) =>
        {
            if (args.CurrentState == FirmwareUpdateState.Complete)
            {
                throw new InvalidOperationException("Subscriber failure on Complete.");
            }
        };

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
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenCanceled_ReportsPic32FlashNotChangedThroughTheEvent()
    {
        var device = new FakeStreamingDevice("COM8");
        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromSeconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var events = new List<FirmwareUpdateStateChangedEventArgs>();
        service.StateChanged += (_, args) => events.Add(args);

        // Cancel as the update-mode request goes out rather than on a timer, so the cancel always
        // lands in the pause before the apply, however slow the runner is.
        using var cts = new CancellationTokenSource();
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:COMMUnicate:LAN:FWUpdate")
            {
                cts.Cancel();
            }
        };
        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        var terminal = events[^1];
        Assert.Equal(FirmwareUpdateState.Failed, terminal.CurrentState);
        Assert.Equal(Pic32FlashCondition.NotChanged, terminal.FlashCondition);
    }

    [Fact]
    public void GetFlashCondition_ClassifiesEveryStateDeliberately()
    {
        // What a failure in each state means for the flash when NO cleanup erase ran and the
        // erase command had been written. Only the states that provably precede the erase may
        // say NotChanged; everything unclassified
        // must fail toward PossiblyHalfFlashed. A new FirmwareUpdateState member fails this test
        // until someone adds it here on purpose.
        var expected = new Dictionary<FirmwareUpdateState, Pic32FlashCondition>
        {
            [FirmwareUpdateState.Idle] = Pic32FlashCondition.NotChanged,
            [FirmwareUpdateState.PreparingDevice] = Pic32FlashCondition.NotChanged,
            [FirmwareUpdateState.WaitingForBootloader] = Pic32FlashCondition.NotChanged,
            [FirmwareUpdateState.Connecting] = Pic32FlashCondition.NotChanged,
            [FirmwareUpdateState.ErasingFlash] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.Programming] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.Verifying] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.JumpingToApplication] = Pic32FlashCondition.ProgrammedNotConfirmed,
            [FirmwareUpdateState.Complete] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.Failed] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.CleaningUp] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.Recovered] = Pic32FlashCondition.PossiblyHalfFlashed,
            [FirmwareUpdateState.ReconnectingAfterFlash] = Pic32FlashCondition.PossiblyHalfFlashed,
        };

        var allStates = Enum.GetValues<FirmwareUpdateState>();
        Assert.Equal(allStates.OrderBy(s => s), expected.Keys.OrderBy(s => s));

        foreach (var state in allStates)
        {
            Assert.Equal(
                expected[state],
                Pic32FirmwareUpdater.GetFlashCondition(
                    state, Pic32FirmwareUpdater.Pic32CleanupOutcome.NotEligible, Pic32FlashProgress.EraseSent));

            // #952: with the erase command never written, the update changed nothing, whatever
            // state it had reached. ErasingFlash is the state this matters for, because it is
            // entered ahead of the write.
            Assert.Equal(
                Pic32FlashCondition.NotChanged,
                Pic32FirmwareUpdater.GetFlashCondition(
                    state, Pic32FirmwareUpdater.Pic32CleanupOutcome.NotEligible, Pic32FlashProgress.NotChanged));

            // Once a cleanup erase ran, its result decides, whatever state the failure was in
            // and whatever the flash record said before it.
            foreach (var flashProgress in Enum.GetValues<Pic32FlashProgress>())
            {
                Assert.Equal(
                    Pic32FlashCondition.CleanBootloader,
                    Pic32FirmwareUpdater.GetFlashCondition(
                        state, Pic32FirmwareUpdater.Pic32CleanupOutcome.Recovered, flashProgress));
                Assert.Equal(
                    Pic32FlashCondition.PossiblyHalfFlashed,
                    Pic32FirmwareUpdater.GetFlashCondition(
                        state, Pic32FirmwareUpdater.Pic32CleanupOutcome.CleanupFailed, flashProgress));
            }
        }
    }

    [Fact]
    public void AmbiguousBootloaderTargetException_ReportsFlashNotChanged()
    {
        // It bypasses the exception factory, so it has to carry the value itself.
        var ex = new AmbiguousBootloaderTargetException(["path-1", "path-2"]);

        Assert.Equal(Pic32FlashCondition.NotChanged, ex.FlashCondition);
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
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
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
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress: progress);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);

        // The canonical WiFi-update prep and post-flash recovery sequences Core now owns end to
        // end (part of #269). The power-on leads the prep because LAN commands are rejected while
        // the WINC is unpowered; the apply after LAN:FWUpdate is what actually enters the
        // USB-to-WINC bridge, since the FWUpdate command only requests it (#853); and the
        // transparent-mode exit leads the recovery because the LAN commands after it would
        // otherwise be forwarded to the WINC instead of interpreted.
        Assert.Equal(
            [
                "SYSTem:POWer:STATe 1",
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:USB:SetTransparentMode 0",
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

        // The post-flash reconnect runs in its own state rather than borrowing Verifying, which
        // means only the PIC32 CRC check (a genuine flash failure) is ever reported as Verifying.
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.ReconnectingAfterFlash);
        Assert.DoesNotContain(progressEvents, p => p.State == FirmwareUpdateState.Verifying);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenPowerOnBeforeUpdateModeDisabled_SkipsPowerOnButStillRestoresTransparentMode()
    {
        // The opt-out exists for consumers that already powered the module on themselves. It must
        // affect the prep half ONLY — the post-flash transparent-mode exit is recovery of state
        // the flash disturbed and is not conditional on it.
        var device = new FakeStreamingDevice("COM31");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["verify passed", "Operation completed successfully"],
                [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);
        options.PowerOnWifiModuleBeforeUpdateMode = false;

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
        Assert.Equal(
            [
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:USB:SetTransparentMode 0",
                "SYSTem:COMMunicate:LAN:ENAbled 1",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:COMMunicate:LAN:SAVE"
            ],
            device.SentCommands);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenCanceledDuringTransparentModeExitSettle_LeavesLanRestoreUnsent()
    {
        // Pins that the settle genuinely sits BETWEEN the transparent-mode exit and the LAN
        // restore, not before the exit or after the whole recovery block: cancellation is raised
        // from inside the transparent-mode Send, so only a wait positioned between the two can
        // stop LAN:ENAbled/APPLY/SAVE from going out. Also proves the wait observes the caller's
        // token rather than sleeping through a cancel.
        using var cts = new CancellationTokenSource();
        var device = new FakeStreamingDevice("COM33");
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:USB:SetTransparentMode 0")
            {
                cts.Cancel();
            }
        };

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["verify passed", "Operation completed successfully"],
                [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);
        // Positive so the success-path settle exists for this cancel to land in. The token is
        // already cancelled when that wait starts, so it ends at once. Kept short on purpose:
        // the failure restore honors the same option and does not observe the cancelled token,
        // so this value is paid in full on the way out.
        options.PostUsbTransparentModeExitDelay = TimeSpan.FromMilliseconds(15);

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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        // The restore is LAN:ENAbled -> APPLY -> SAVE, and the settle holds back all three: not one
        // of them is here. Two other applies are, and neither is part of a restore. The first is
        // prep's, which enters the bridge. The last is the failure recovery's bridge-exit kick,
        // which the cancel itself arms; two things tell it apart from a restore that leaked half
        // way out — it re-sends the transparent-mode exit ahead of itself, and it is never
        // accompanied by ENAbled or SAVE. It persists nothing, so a canceled flash still cannot
        // write a network configuration.
        Assert.Equal(
            [
                "SYSTem:POWer:STATe 1",
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:USB:SetTransparentMode 0",
                "SYSTem:USB:SetTransparentMode 0",
                "SYSTem:COMMunicate:LAN:APPLY"
            ],
            device.SentCommands);

        // State the guarantee this test exists for on its own terms, not as a side effect of the
        // sequence above. The Assert.Equal already implies both of these today, but it is the
        // assertion most likely to be edited: every change to the recovery sequence rewrites that
        // list, and whoever rewrites it is thinking about the commands they are adding, not about
        // the two that must never appear. Spelled out separately, a cancel that starts persisting
        // a network configuration fails on a line that says so, instead of looking like one more
        // expected-list update to wave through.
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", device.SentCommands);
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:SAVE", device.SentCommands);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenTransparentModeExitDelayIsZero_StillSendsFullRecoverySequence()
    {
        // Zero means "skip the wait", not "skip the step". A consumer that collapses the delay
        // (or a transport that needs no pacing) must still get the whole recovery sequence.
        var device = new FakeStreamingDevice("COM34");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["verify passed", "Operation completed successfully"],
                [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);
        options.PostUsbTransparentModeExitDelay = TimeSpan.Zero;

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
        Assert.Equal(
            [
                "SYSTem:POWer:STATe 1",
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:USB:SetTransparentMode 0",
                "SYSTem:COMMunicate:LAN:ENAbled 1",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:COMMunicate:LAN:SAVE"
            ],
            device.SentCommands);
    }

    [Fact]
    public void FirmwareUpdateServiceOptions_PostUsbTransparentModeExitDelay_DefaultsToBridgeDeactivationPace()
    {
        // The default is the behavior: a consumer that upgrades without touching options gets
        // the pacing on both the success restore and the failure bridge-exit. 100ms is not
        // arbitrary — it is the same inter-command wait WifiBridgeActivator.DeactivateAsync already
        // uses for this exact mode transition over a raw serial port, so the managed-connection
        // paths stay paced identically until a host overrides the option. Pinned so the default
        // cannot silently drift apart from that raw-serial pace.
        var options = new FirmwareUpdateServiceOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.PostUsbTransparentModeExitDelay);
        Assert.Equal(WifiBridgeActivator.InterCommandDelay, options.PostUsbTransparentModeExitDelay);
        options.Validate();
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenFlashFails_FailureRestoreHonorsPostUsbTransparentModeExitDelay()
    {
        // The success path already waits PostUsbTransparentModeExitDelay. The failure restore
        // used to wait the fixed WifiBridgeActivator.InterCommandDelay (100ms) instead, so a
        // host that raised the option still got the short pause on the path that most needs it.
        // 250ms is neither the default nor that fixed pace: a gap under it means the failure
        // path ignored the option. The caller token is cancelled before the restore, and the
        // recovery budget (50ms) expires during the settle. Either one aborting the wait would
        // skip LAN:APPLY or collapse the gap to the budget.
        var delay = TimeSpan.FromMilliseconds(250);
        Assert.NotEqual(WifiBridgeActivator.InterCommandDelay, delay);

        using var cts = new CancellationTokenSource();
        var device = new FakeStreamingDevice("COM36");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? transparentModeExitAt = null;
        TimeSpan? lanApplyAt = null;
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:COMMunicate:LAN:APPLY")
            {
                if (transparentModeExitAt is null)
                {
                    // Prep's apply, the one that enters the bridge. Cancelling on it puts the
                    // failure after the bridge entry, which is the state whose recovery sends the
                    // two-command exit this case measures. The apply recorded below is the other
                    // one — the exit's — and it only exists in this state (#853).
                    cts.Cancel();
                }
                else
                {
                    lanApplyAt ??= clock.Elapsed;
                }
            }
            else if (command == "SYSTem:USB:SetTransparentMode 0")
            {
                transparentModeExitAt ??= clock.Elapsed;
            }
        };

        var options = CreateFastOptions();
        options.PostUsbTransparentModeExitDelay = delay;
        // The await right after the bridge-entering apply, so the cancel requested on that apply
        // is observed there rather than racing the rest of prep.
        options.PostWifiBridgeEntryDelay = TimeSpan.FromMilliseconds(10);
        // Shorter than the settle, so a wait that observes the recovery budget cannot
        // satisfy the gap assertion below — and cannot send LAN:APPLY either.
        options.VerifyingTimeout = TimeSpan.FromMilliseconds(50);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Equal(
            [
                "SYSTem:POWer:STATe 1",
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:USB:SetTransparentMode 0",
                "SYSTem:COMMunicate:LAN:APPLY"
            ],
            device.SentCommands);
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", device.SentCommands);
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:SAVE", device.SentCommands);

        Assert.NotNull(transparentModeExitAt);
        Assert.NotNull(lanApplyAt);
        var gap = lanApplyAt.Value - transparentModeExitAt.Value;
        Assert.True(
            gap >= delay - TimeSpan.FromMilliseconds(40),
            $"Failure restore waited {gap.TotalMilliseconds:0}ms between the transparent-mode exit and LAN:APPLY; " +
            $"expected at least {delay.TotalMilliseconds:0}ms (PostUsbTransparentModeExitDelay), not the fixed 100ms bridge pace.");
    }

    [Fact]
    public void FirmwareUpdateServiceOptions_Validate_RejectsNegativeTransparentModeExitDelayButAllowsZero()
    {
        // Zero is the documented "skip the wait" escape hatch and must stay legal; a negative
        // value is a misconfiguration that would otherwise reach Task.Delay.
        var options = new FirmwareUpdateServiceOptions { PostUsbTransparentModeExitDelay = TimeSpan.Zero };
        options.Validate();

        options.PostUsbTransparentModeExitDelay = TimeSpan.FromMilliseconds(-1);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal(nameof(FirmwareUpdateServiceOptions.PostUsbTransparentModeExitDelay), exception.ParamName);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenCanceledDuringPowerOnSettle_NeverEntersLanUpdateMode()
    {
        // Pins that the settle delay is genuinely awaited BETWEEN the power-on and the
        // update-mode command, not before both or after both: cancellation is raised from
        // inside the power-on Send, so only a wait sitting between the two sends can stop
        // LAN:FWUpdate from going out. Also proves the wait observes the caller's token
        // instead of sleeping the device into update mode after a cancel.
        using var cts = new CancellationTokenSource();
        var device = new FakeStreamingDevice("COM32");
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:POWer:STATe 1")
            {
                cts.Cancel();
            }
        };

        var options = CreateFastOptions();
        // Long enough that the cancel, not the elapsed time, is what ends the wait — and long
        // enough that a missing wait would let LAN:FWUpdate slip out before the cancel is seen.
        options.PowerOnWifiModuleSettleDelay = TimeSpan.FromSeconds(30);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(["SYSTem:POWer:STATe 1"], device.SentCommands);
        Assert.Equal(0, device.DisconnectCalls);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenCanceledDuringVersionCheck_SendsNoPreparationCommands()
    {
        // A cancel that lands after the operation lock is taken but before device prep must not
        // still power the module on or flip the device into LAN firmware-update mode — update
        // mode leaves the device unusable until it is taken back out of it, which a canceled
        // caller will not be around to do. The version check is the real window for this: it
        // returns a status object rather than observing the token itself, so without an explicit
        // check the prep step runs both Sends before its first await notices the cancellation.
        using var cts = new CancellationTokenSource();

        var downloadService = new FakeFirmwareDownloadService
        {
            // Cancel from inside the check, then return normally: the check completes, decides a
            // flash is needed (19.5.4 is below the supported minimum), and hands control to prep.
            OnGetLatestWifiRelease = cts.Cancel
        };

        var device = new FakeWifiChipInfoStreamingDevice(
            "COM33",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        // Keeps SentCommands to just the prep commands under test — the probe's own power-on is
        // a different code path with its own coverage.
        options.PowerOnWifiModuleBeforeProbe = false;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public void FirmwareUpdateServiceOptions_PowerOnWifiModuleBeforeUpdateMode_DefaultsToTrue()
    {
        // The default is the behavior change: a consumer that upgrades without touching options
        // gets the power-on prep. Pinned so it cannot be flipped off silently.
        var options = new FirmwareUpdateServiceOptions();

        Assert.True(options.PowerOnWifiModuleBeforeUpdateMode);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PowerOnWifiModuleSettleDelay);
        options.Validate();
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenPostFlashReconnectTimesOut_FailsInReconnectingAfterFlash()
    {
        // The WINC image flashed and verified fine; only the host's serial re-enumeration timed
        // out. That is benign and environmental, and used to be indistinguishable from a PIC32
        // flash CRC mismatch — both arrived as FailedState = Verifying, leaving a consumer to
        // discriminate on a human-readable progress string and telling a user whose flash
        // succeeded that their flash CRC did not match (#398 gap 4).
        var device = new FakeStreamingDevice("COM9")
        {
            // Never reconnects, so the state timeout is the only way out.
            ConnectFailuresBeforeSuccess = int.MaxValue
        };
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["begin write operation", "begin verify operation", "verify passed", "Operation completed successfully"],
                [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);
        // Also the ReconnectingAfterFlash budget — the step kept the timeout it had when it ran
        // under Verifying, so a host that tuned VerifyingTimeout keeps that tuning.
        options.VerifyingTimeout = TimeSpan.FromMilliseconds(300);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            // The structural discriminator: the state itself, not the Operation progress string.
            Assert.Equal(FirmwareUpdateState.ReconnectingAfterFlash, exception.FailedState);
            Assert.Contains(FirmwareUpdateState.ReconnectingAfterFlash, stateTransitions);

            // Guidance is keyed on the same discriminator, so a successful flash no longer tells
            // the user their flash CRC mismatched.
            Assert.NotNull(exception.RecoveryGuidance);
            Assert.Contains("flashed and verified successfully", exception.RecoveryGuidance);
            Assert.DoesNotContain("CRC", exception.RecoveryGuidance);

            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
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
            // #944: a WiFi module update never writes the PIC32 application flash.
            Assert.Equal(Pic32FlashCondition.NotChanged, exception.FlashCondition);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenFlashToolFails_TakesDeviceBackOutOfLanUpdateMode()
    {
        // Once LAN:FWUpdate lands the device bypasses its SCPI console and bridges USB straight
        // to the WINC. Only the success path used to walk it back out, so a failed flash left the
        // module unreachable until someone power-cycled it — which is why daqifi-desktop still
        // wraps this call in its own recovery finally (#269 item 2).
        var device = new FakeStreamingDevice("COM11");
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

            // The recovery must not swallow or reshape the real failure.
            Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);

            // Same two commands, in the same order, as the raw-serial WifiBridgeActivator.DeactivateAsync
            // twin: hand the port back to the SCPI console, then kick the WiFi manager out of its
            // bridge-mode state machine. Deliberately no LAN:ENAbled/SAVE — persisting a network
            // configuration off the back of a flash that did not complete is not this step's job.
            // The leading power-on and apply are prep's, not the recovery's: LAN commands are
            // rejected while the WINC is unpowered, so LAN:FWUpdate has to be preceded by the
            // power-on, and the apply after it is what enters the bridge the recovery then leaves.
            Assert.Equal(
                [
                    "SYSTem:POWer:STATe 1",
                    "SYSTem:COMMUnicate:LAN:FWUpdate",
                    "SYSTem:COMMunicate:LAN:APPLY",
                    "SYSTem:USB:SetTransparentMode 0",
                    "SYSTem:COMMunicate:LAN:APPLY"
                ],
                device.SentCommands);

            // Prep disconnected the device to hand the port to the flash tool, so the recovery had
            // to bring the transport back before it could send anything.
            Assert.Equal(1, device.DisconnectCalls);
            Assert.True(device.ConnectAttempts >= 1);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenCanceledAfterRequestingUpdateMode_RebootsToClearTheRequest()
    {
        // LAN:FWUpdate is already on the wire, but the apply that acts on it is not, so the device
        // is still answering its own console with the firmware's update request held. An apply
        // from here would ENTER the bridge, and simply handing the port back would leave the
        // request armed for the next LAN:APPLY from anywhere (#853). The flag is WiFi-manager RAM
        // state and the firmware clears it only on the way out of the bridge, so recovery reboots
        // instead. The caller's token is canceled by definition here, so the recovery runs on a
        // token of its own or it would never send anything.
        var device = new FakeStreamingDevice("COM12");

        var options = CreateFastOptions();
        // Long enough that cancellation lands inside it, and inside the 2s PreparingDeviceTimeout
        // so the state timeout is not what ends the wait. This is the pace between the request and
        // the apply, so a cancel here is a cancel before the bridge.
        options.PostWifiFirmwareModeDelay = TimeSpan.FromSeconds(1);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        // Cancel as the update-mode request goes out rather than on a timer, so the cancel always
        // lands in the pause before the apply, however slow the runner is.
        using var cts = new CancellationTokenSource();
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:COMMUnicate:LAN:FWUpdate")
            {
                cts.Cancel();
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));

            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);

            Assert.Equal(
                [
                    "SYSTem:POWer:STATe 1",
                    "SYSTem:COMMUnicate:LAN:FWUpdate",
                    "SYSTem:USB:SetTransparentMode 0",
                    "SYSTem:REboot"
                ],
                device.SentCommands);

            // Said on its own terms: this is the one state where an apply is the wrong command,
            // and the reboot is what clears the request it would otherwise act on.
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:APPLY", device.SentCommands);
            Assert.Equal("SYSTem:REboot", device.SentCommands[^1]);

            // Cancellation landed before prep reached the disconnect, so the recovery found the
            // transport already up — the exit is not conditional on having been disconnected.
            Assert.Equal(0, device.DisconnectCalls);
            Assert.Equal(0, device.ConnectAttempts);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenRecoveryBudgetExpiresAfterReconnect_StillFinishesTheBridgeExit()
    {
        // The recovery budget bounds the wait for the transport to come back, not the two-command
        // exit that follows it. The pause is PostUsbTransparentModeExitDelay, and it outlasts the
        // budget (50ms): by the time it elapses the budget is already spent, and LAN:APPLY still
        // goes out. Stopping half way would hand the console back while leaving the WiFi manager
        // in its bridge-mode state machine, which is a worse place to leave the device than
        // either end of the sequence.
        using var cts = new CancellationTokenSource();
        var device = new FakeStreamingDevice("COM13");

        // The two-command exit only runs when the device may be bridged, so the cancel has to land
        // after prep's bridge-entering apply. Requested on that apply and observed in the settle
        // that follows it, which makes the case hold by construction rather than on a timer.
        device.OnCommandSent = command =>
        {
            if (command == "SYSTem:COMMunicate:LAN:APPLY")
            {
                cts.Cancel();
            }
        };

        var options = CreateFastOptions();
        options.PostWifiBridgeEntryDelay = TimeSpan.FromMilliseconds(10);
        // Also the ReconnectingAfterFlash budget, which is what bounds the recovery.
        options.VerifyingTimeout = TimeSpan.FromMilliseconds(50);
        // Longer than that budget. A settle that observed it would be cancelled before LAN:APPLY.
        options.PostUsbTransparentModeExitDelay = TimeSpan.FromMilliseconds(150);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            // The original cancellation still surfaces — the recovery must never turn into the
            // exception the caller sees.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir, cancellationToken: cts.Token));

            Assert.Equal(
                [
                    "SYSTem:POWer:STATe 1",
                    "SYSTem:COMMUnicate:LAN:FWUpdate",
                    "SYSTem:COMMunicate:LAN:APPLY",
                    "SYSTem:USB:SetTransparentMode 0",
                    "SYSTem:COMMunicate:LAN:APPLY"
                ],
                device.SentCommands);

            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenRecoveryBudgetExpiresWaitingForReconnect_SendsNoBridgeExit()
    {
        // The one thing the budget does bound: a transport that never comes back. Prep hands the
        // port to the flash tool, the flash fails, and the device then refuses every reconnect —
        // the recovery must give up after the budget instead of waiting forever, and must not
        // pretend to send commands down a transport that is not there.
        var device = new FakeStreamingDevice("COM14")
        {
            // Consumed one per Connect() attempt; far more than the budget allows, so the
            // reconnect loop never succeeds.
            ConnectFailuresBeforeSuccess = int.MaxValue
        };

        var options = CreateFastOptions();
        // Also the ReconnectingAfterFlash budget, which is what bounds the recovery.
        options.VerifyingTimeout = TimeSpan.FromMilliseconds(50);

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
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            // The flash failure, not the recovery's timeout, is what the caller sees.
            Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);

            // Prep's power-on, LAN:FWUpdate and the apply that enters the bridge, and nothing after
            // them: the recovery gave up in the reconnect wait, so neither half of the bridge exit
            // was sent down a dead transport.
            Assert.Equal(
                [
                    "SYSTem:POWer:STATe 1",
                    "SYSTem:COMMUnicate:LAN:FWUpdate",
                    "SYSTem:COMMunicate:LAN:APPLY"
                ],
                device.SentCommands);
            Assert.True(device.ConnectAttempts >= 1);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenDeviceIsNotConnected_DoesNotAttemptTheBridgeExit()
    {
        // The connectivity precondition fails with the update-mode command definitively un-sent,
        // so there is no bridge to leave. Arming the recovery for it would turn an immediate
        // "device must be connected" failure into a full ReconnectingAfterFlash wait (45s by
        // default) for a transport that was never gone.
        var device = new FakeStreamingDevice("COM15");
        device.Disconnect();

        var options = CreateFastOptions();
        // Deliberately generous: if the recovery were armed here, its reconnect loop would run for
        // this long and the assertion on elapsed time below would catch it.
        options.VerifyingTimeout = TimeSpan.FromSeconds(30);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            stopwatch.Stop();

            Assert.Equal(FirmwareUpdateState.PreparingDevice, exception.FailedState);

            // Nothing was sent, and — the point of the test — nothing was retried either. The
            // device this fake models is one that would reconnect on the very first attempt, so a
            // single Connect() call would be enough to mask the regression; assert zero.
            Assert.Empty(device.SentCommands);
            Assert.Equal(0, device.ConnectAttempts);

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Expected an immediate failure, took {stopwatch.Elapsed}.");
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
        options.WifiBridgeActivationCallback = _ =>
        {
            bridgeActivations++;
            return Task.CompletedTask;
        };

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
    public async Task UpdateWifiModuleAsync_WhenDeviceVersionMatchesLatest_SkipsFlashAndReportsComplete()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };

        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease };
        var device = new FakeWifiChipInfoStreamingDevice("COM9", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
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
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress: progress);
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
    public async Task UpdateWifiModuleAsync_WhenLatestReleaseUnavailableButDeviceMeetsMinimum_SkipsFlash()
    {
        // The defect this closes: a release lookup failure is a *network* failure, but it
        // used to fall through to "flash conservatively" — erasing and reprogramming a WINC
        // module whose own reported version was already supported, for eight minutes, because
        // GitHub was unreachable. The device answered; that answer is enough.
        var downloadService = new FakeFirmwareDownloadService
        {
            LatestWifiReleaseException = new HttpRequestException("no route to host")
        };
        var processRunner = new FakeExternalProcessRunner();
        var device = new FakeWifiChipInfoStreamingDevice("COM47", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.7.7",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            processRunner,
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
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress: progress);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        // The strongest assertion available: the flash tool was never launched at all.
        Assert.Equal(0, processRunner.RunCount);
        Assert.DoesNotContain("SYSTem:COMMUnicate:LAN:FWUpdate", device.SentCommands);

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        var completeEvent = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, completeEvent.PercentComplete);
        Assert.Contains("minimum supported version", completeEvent.CurrentOperation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenLatestReleaseUnavailableAndDeviceBelowMinimum_ProceedsWithFlash()
    {
        // The guard on the skip above: it is a verdict, not a blanket "give up and assume
        // fine". A module genuinely below the supported minimum still gets flashed even
        // though the release lookup failed.
        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = null };
        var processRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0, false, TimeSpan.Zero, ["Operation completed successfully"], [])
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM48", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            processRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

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

        Assert.True(processRunner.RunCount > 0);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenChipInfoUnavailable_StillProceedsWithFlash()
    {
        // Regression guard for the null case: "could not read the device" must never be
        // mistaken for "meets the minimum". This is the path that would silently stop
        // flashing broken modules if the three-state verdict were collapsed to a bool.
        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = null };
        var processRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0, false, TimeSpan.Zero, ["Operation completed successfully"], [])
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM49", chipInfo: null);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            downloadService,
            processRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

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

        Assert.True(processRunner.RunCount > 0);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenDeviceVersionIsOlder_ProceedsWithFlash()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 6, 1, 0, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };

        var downloadService = new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease };
        var device = new FakeWifiChipInfoStreamingDevice("COM10", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
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
    public async Task UpdateWifiModuleAsync_WhenDeviceDoesNotSupportWifiQuery_ProceedsWithFlash()
    {
        // FakeStreamingDevice does NOT implement IWifiChipInfoProvider — version check is skipped
        var device = new FakeStreamingDevice("COM11");

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
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

    /// <summary>
    /// The application boots dormant, so the update initializes the device it reconnected to, through
    /// <see cref="IDevice"/> and so whatever its class (issue #1038). A failure there does not fail
    /// an update that has already been written, and neither does a cancellation of the device's own.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task UpdateFirmwareAsync_InitializesTheReconnectedDevice(Type? failure)
    {
        var device = new FakeStreamingDevice("COM3")
        {
            OnInitialize = _ => failure is null
                ? Task.CompletedTask
                : Task.FromException((Exception)Activator.CreateInstance(failure)!)
        };
        var service = CreateUpdateThatReconnects(CreateFastOptions());

        await RunUpdateAsync(service, device);

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(1, device.InitializeCalls);
    }

    /// <summary>
    /// A caller who cancels while the reconnected device is initializing hears of the cancel, whatever
    /// the interrupted initialization threw. It was logged like any other failure, and the update
    /// reported as completed. No probe, so nothing after it observes the token.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateFirmwareAsync_CanceledWhileInitializingTheReconnectedDevice_ReportsTheCancel(
        bool initializeThrowsTheCancel)
    {
        using var cts = new CancellationTokenSource();
        var device = new FakeStreamingDevice("COM3")
        {
            OnInitialize = token =>
            {
                cts.Cancel();
                return initializeThrowsTheCancel
                    ? Task.FromCanceled(token)
                    : Task.FromException(new DeviceNotConnectedException("torn down by the cancel"));
            }
        };
        var service = CreateUpdateThatReconnects(CreateFastOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunUpdateAsync(service, device, cts.Token));

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Equal(1, device.InitializeCalls);
    }

    /// <summary>
    /// The budget for the whole jump-and-reconnect step running out during the initialization is that
    /// step's timeout, not a completed update.
    /// </summary>
    [Fact]
    public async Task UpdateFirmwareAsync_StepTimesOutWhileInitializingTheReconnectedDevice_ReportsTheTimeout()
    {
        var options = CreateFastOptions();
        // Covers the reconnect too, so it is long enough that only the initialization can use it up.
        options.JumpingToApplicationTimeout = TimeSpan.FromSeconds(1);
        var device = new FakeStreamingDevice("COM3")
        {
            OnInitialize = token => Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        var service = CreateUpdateThatReconnects(options);

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(() => RunUpdateAsync(service, device));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(FirmwareUpdateState.JumpingToApplication, ex.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.Equal(1, device.InitializeCalls);
    }

    /// <summary>A service whose bootloader accepts a whole update, through to the jump.</summary>
    private static FirmwareUpdateService CreateUpdateThatReconnects(FirmwareUpdateServiceOptions options)
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        return new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01], [0xA1, 0x02]]),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            options);
    }

    private static async Task RunUpdateAsync(
        FirmwareUpdateService service,
        IStreamingDevice device,
        CancellationToken cancellationToken = default)
    {
        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath, cancellationToken: cancellationToken);
        }
        finally
        {
            File.Delete(hexPath);
        }
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
        // Both budgets are deliberately generous. What this test asserts is behavioural
        // — the probe is polled until it returns true, and Complete is held back until
        // then — so the budgets exist only to stop a hung probe from hanging the suite,
        // never as a deadline the assertions depend on. The probe succeeds after two
        // 10ms retry delays, so the headroom costs nothing in the normal case: it just
        // stops a stalled CI runner from failing a behavioural assertion on wall clock.
        // A 1s budget here did exactly that — a loaded runner got only 2 of the 3
        // probes away inside the second, and the timeout fired one probe short. The
        // readiness budget still stays strictly below JumpingToApplicationTimeout, which
        // FirmwareUpdateServiceOptions.Validate requires whenever a probe is set.
        //
        // 10s/15s rather than something enormous: these also bound how long a real
        // regression in the JumpingToApplication state takes to surface, so the aim is comfortably
        // clear of a stalled runner without going so far that a genuine hang is slow to
        // diagnose. 10s is an order of magnitude past the ~1s stall that broke the 1s
        // budget, and ~300x what the probe actually needs.
        options.JumpingToApplicationTimeout = TimeSpan.FromSeconds(15);
        options.PostReconnectReadinessTimeout = TimeSpan.FromSeconds(10);
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
        Assert.Equal(FirmwareUpdateState.JumpingToApplication, ex.FailedState);
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
        // Generous budgets for the same reason as
        // UpdateFirmwareAsync_PostReconnectReadinessProbe_AwaitedBeforeComplete: the
        // assertion is that the retry happens and the update still completes, not that
        // it completes inside a deadline. Leaving a 1s budget here makes a behavioural
        // test fail whenever the runner stalls long enough to eat the third probe.
        options.JumpingToApplicationTimeout = TimeSpan.FromSeconds(15);
        options.PostReconnectReadinessTimeout = TimeSpan.FromSeconds(10);
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
        // JumpingToApplication error instead of the readiness-specific one. Validate()
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
            Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM9", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
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
        Assert.Equal("19.5.4", status.CurrentChipInfo!.FirmwareVersion);
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
            Version = new FirmwareVersion(19, 6, 1, 0, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM10", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
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
    public async Task CheckWifiFirmwareStatusAsync_WhenDeviceDoesNotSupportWifiQuery_ReturnsDeviceDoesNotSupportWifiQuery()
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
        Assert.Equal(WifiFirmwareStatusReason.DeviceDoesNotSupportWifiQuery, status.Reason);
        Assert.Null(status.CurrentChipInfo);
        Assert.Null(status.LatestRelease);

        // The bar is a property of Core's policy, not of the device, so it is reported
        // even when no device version was ever read. The verdict is not.
        Assert.Equal(new FirmwareVersion(19, 7, 7, 0, null, 0), status.MinimumSupportedVersion);
        Assert.Null(status.MeetsMinimumSupportedVersion);
    }

    [Fact]
    public void MinimumSupportedWifiFirmwareVersion_DefaultsToTheFirmwareContractValue()
    {
        // 19.7.7 is a firmware-contract fact (#269), not a tuning default. Pinned here so a
        // change to it is a deliberate edit to this assertion rather than a silent drift that
        // would start accepting or rejecting modules in the field.
        Assert.Equal(new FirmwareVersion(19, 7, 7, 0, null, 0), FirmwareUpdateServiceOptions.DefaultMinimumSupportedWifiFirmwareVersion);
        Assert.Equal(
            FirmwareUpdateServiceOptions.DefaultMinimumSupportedWifiFirmwareVersion,
            new FirmwareUpdateServiceOptions().MinimumSupportedWifiFirmwareVersion);
    }

    [Fact]
    public void BootloaderIds_DefaultToTheOnesDiscoveryLooksFor()
    {
        var options = new FirmwareUpdateServiceOptions();
        using var finder = new HidDeviceFinder();

        Assert.Equal(finder.VendorIdFilter, options.BootloaderVendorId);
        Assert.Equal(finder.ProductIdFilter, options.BootloaderProductId);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenLatestReleaseLookupReturnsNull_StillAnswersMinimumSupported()
    {
        // The point of the minimum: a release lookup that fails (offline bench, blocked
        // egress, rate limit) leaves IsUpToDate unanswerable, but the device already told
        // us its version, so "is this module supported" is still answerable — with no
        // network at all.
        var device = new FakeWifiChipInfoStreamingDevice("COM40", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.7.7",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = null },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        // Unchanged: IsUpToDate stays a strictly latest-release answer and stays false.
        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.LatestReleaseUnavailable, status.Reason);
        Assert.Null(status.LatestRelease);

        // New: the network-independent verdict.
        Assert.True(status.MeetsMinimumSupportedVersion);
        Assert.Equal(new FirmwareVersion(19, 7, 7, 0, null, 0), status.MinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenLatestReleaseLookupThrows_StillAnswersMinimumSupported()
    {
        // The throwing half of the same failure — a different catch block in the
        // implementation, so it needs its own coverage.
        var device = new FakeWifiChipInfoStreamingDevice("COM41", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.8.0",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService
            {
                LatestWifiReleaseException = new HttpRequestException("no route to host")
            },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(WifiFirmwareStatusReason.LatestReleaseUnavailable, status.Reason);
        Assert.False(status.IsUpToDate);
        Assert.True(status.MeetsMinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenDeviceIsBelowMinimum_ReportsFalseNotNull()
    {
        // "Below the minimum" and "could not tell" must stay distinguishable — collapsing
        // them is the conflation the three-state property exists to prevent.
        var device = new FakeWifiChipInfoStreamingDevice("COM42", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = null },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        // Assert.False on a bool? already rejects null, so this alone pins "false, not unknown".
        Assert.False(status.MeetsMinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenChipInfoUnavailable_LeavesMinimumVerdictUnknown()
    {
        var device = new FakeWifiChipInfoStreamingDevice("COM43", chipInfo: null);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = null },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, status.Reason);
        Assert.Null(status.MeetsMinimumSupportedVersion);
        Assert.Equal(new FirmwareVersion(19, 7, 7, 0, null, 0), status.MinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenDeviceVersionUnparseable_LeavesMinimumVerdictUnknown()
    {
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 7, 7, 0, null, 0),
            TagName = "19.7.7",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM44", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "garbage",
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

        Assert.Equal(WifiFirmwareStatusReason.VersionUnparseable, status.Reason);
        Assert.Null(status.MeetsMinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_UsesTheConfiguredMinimum_NotOnlyTheDefault()
    {
        // A manufacturing line raising the bar must actually move the verdict; a device that
        // passes the shipped default has to fail a higher configured minimum.
        var options = CreateFastOptions();
        options.MinimumSupportedWifiFirmwareVersion = new FirmwareVersion(19, 9, 0, 0, null, 0);

        var device = new FakeWifiChipInfoStreamingDevice("COM45", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.7.7",
            BuildDate = "Jan  8 2019"
        });

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = null },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(new FirmwareVersion(19, 9, 0, 0, null, 0), status.MinimumSupportedVersion);
        Assert.False(status.MeetsMinimumSupportedVersion);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenLatestReleaseIsKnown_StillReportsTheMinimumVerdict()
    {
        // The minimum is reported on the happy path too, so a caller can use one property
        // consistently instead of switching on Reason to know whether it is populated.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 8, 0, 0, null, 0),
            TagName = "19.8.0",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM46", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.7.7",
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

        // Supported, but not the newest — the two questions genuinely disagree here, which
        // is the case that motivated modeling them separately.
        Assert.Equal(WifiFirmwareStatusReason.UpdateAvailable, status.Reason);
        Assert.False(status.IsUpToDate);
        Assert.True(status.MeetsMinimumSupportedVersion);
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
            Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM12", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4", // Matches latest — would normally short-circuit
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
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
                new WifiUpdateOptions { SkipVersionCheck = true });
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        // Even though device version matched latest, flash ran because
        // SkipVersionCheck bypassed the probe.
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
            Version = new FirmwareVersion(19, 6, 1, 0, null, 0),
            TagName = "19.6.1",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice("COM13", chipInfo: new WifiChipInfo
        {
            ChipId = 1234,
            FirmwareVersion = "19.5.4",
            BuildDate = "Jan  8 2019"
        });

        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(0, false, TimeSpan.FromMilliseconds(10), ["verify passed", "Operation completed successfully"], [])
        };

        var options = CreateFastOptions();
        options.PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
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
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress: progress)
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
    public async Task CheckWifiFirmwareStatusAsync_WhenWifiChipInfoFailsTransiently_RetriesUntilSuccess()
    {
        // Closes #144: post-PIC32-reboot the WiFi subsystem can lag the
        // application by a few seconds, so the first chip-info query
        // transiently fails. Without retry, the WiFi version decision
        // would short-circuit and trigger an unnecessary multi-minute
        // reflash. The retry budget covers the startup window.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM14",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            },
            transientFailuresBeforeSuccess: 2);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5); // Keep test fast

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.True(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenWifiChipInfoFailsAllAttempts_ReturnsChipInfoUnavailable()
    {
        // After exhausting WifiChipInfoMaximumAttempts the planning method must
        // fall through to ChipInfoUnavailable, NOT hang or surface the
        // raw exception. This preserves the "couldn't check, default to
        // running update" semantics for genuinely-broken devices.
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM15",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            },
            transientFailuresBeforeSuccess: 99);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenWifiNotInitialized_KicksLanApplyOnceAndRecovers()
    {
        // Closes #203: LAN:ENAbled=1 in saved settings but the WINC1500 state
        // machine hasn't reached INITIALIZED yet makes GETChipInfo? return
        // SCPI -200 instead of JSON. A single LAN:APPLY resolves it; the
        // retry loop must send that kick exactly once and then recover.
        var wifiRelease = new FirmwareReleaseInfo
        {
            Version = new FirmwareVersion(19, 7, 7, 0, null, 0),
            TagName = "19.7.7",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM22",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1377184,
                FirmwareVersion = "19.7.7",
                BuildDate = "Mar 30 2022"
            },
            wifiNotInitializedFailuresBeforeSuccess: 2);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);
        options.KickWifiApplyOnNotInitialized = true;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(1, device.LanApplySentCount);
        Assert.True(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_WhenWifiNotInitializedExhaustsRetries_ReturnsWifiNotInitialized()
    {
        // If the kick doesn't help within the retry budget, the caller should
        // get the more specific WifiNotInitialized reason instead of the
        // generic ChipInfoUnavailable — same "proceed with flash" behavior,
        // better diagnostics.
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM23",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1377184,
                FirmwareVersion = "19.7.7",
                BuildDate = "Mar 30 2022"
            },
            wifiNotInitializedFailuresBeforeSuccess: 99);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);
        options.KickWifiApplyOnNotInitialized = true;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(1, device.LanApplySentCount);
        Assert.False(status.IsUpToDate);
        Assert.Equal(WifiFirmwareStatusReason.WifiNotInitialized, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_KickWifiApplyDisabled_DoesNotSendApplyOnNotInitialized()
    {
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM24",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1377184,
                FirmwareVersion = "19.7.7",
                BuildDate = "Mar 30 2022"
            },
            wifiNotInitializedFailuresBeforeSuccess: 99);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);
        options.KickWifiApplyOnNotInitialized = false;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(0, device.LanApplySentCount);
        Assert.Equal(WifiFirmwareStatusReason.WifiNotInitialized, status.Reason);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_CancelledDuringWifiNotInitializedHandling_DoesNotSendLanApply()
    {
        // A cancellation race between the WifiNotInitialized detection and the
        // APPLY kick must not still send the state-changing command — mirrors
        // the guard already required before the WINC power-on Send.
        using var cts = new CancellationTokenSource();
        var device = new CancelingWifiChipInfoStreamingDevice("COM25", cts);

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);
        options.KickWifiApplyOnNotInitialized = true;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CheckWifiFirmwareStatusAsync(device, cts.Token));

        Assert.Equal(0, device.LanApplySentCount);
    }

    /// <summary>
    /// Cancels the supplied <see cref="CancellationTokenSource"/> as soon as
    /// <see cref="GetWifiChipInfoAsync"/> is called, then throws
    /// <see cref="WifiNotInitializedException"/> — simulates cancellation racing
    /// with the not-initialized detection, before the retry loop's APPLY kick.
    /// </summary>
    private sealed class CancelingWifiChipInfoStreamingDevice : IStreamingDevice, IWifiChipInfoProvider
    {
        private readonly CancellationTokenSource _cts;

        public CancelingWifiChipInfoStreamingDevice(string name, CancellationTokenSource cts)
        {
            Name = name;
            _cts = cts;
            IsConnected = true;
        }

        public int LanApplySentCount { get; private set; }

        public string Name { get; }
        public bool IsConnected { get; private set; }
        public DeviceState State => DeviceState.Connected;
        public DeviceMetadata Metadata { get; } = new();
        public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();
        public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();
        public int SampleRateHz { get; set; }
        public bool IsStreaming { get; private set; }
        public event EventHandler<DeviceStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated { add { } remove { } }
        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            return ValueTask.CompletedTask;
        }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> textMessage && textMessage.Data == "SYSTem:COMMunicate:LAN:APPLY")
            {
                LanApplySentCount++;
            }
        }

        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IDigitalChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IDigitalChannel channel, bool value) { }
        public void SetPwmEnabled(IDigitalChannel channel, bool enabled) { }
        public void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent) { }
        public void SetPwmFrequency(int frequencyHz) { }
        public int PwmFrequencyHz => 0;
        public void SetAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public int PwmFrequencyFloorHz => 0;
        public void StageAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public void LatchAnalogOutputs() { }
        public Task<double> GetAnalogOutputAsync(IAnalogOutputChannel channel, CancellationToken cancellationToken = default) => Task.FromResult(0.0);
        public bool Supports(DeviceFeature feature) => false;
        public Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshDeviceStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapabilityDocument?> ReadCapabilityDocumentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapabilityDocument?>(null);
        public void Reboot() => IsConnected = false;
        public Task StartStreamingAsync(CancellationToken cancellationToken = default) { StartStreaming(); return Task.CompletedTask; }
        public Task StopStreamingAsync(CancellationToken cancellationToken = default) { StopStreaming(); return Task.CompletedTask; }
        public Task RebootAsync(CancellationToken cancellationToken = default) { Reboot(); return Task.CompletedTask; }

        public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationSlopeAsync(int channelNumber, double slope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationOffsetAsync(int channelNumber, double offset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WifiChipInfo?> GetWifiChipInfoAsync(CancellationToken cancellationToken = default)
        {
            _cts.Cancel();
            return Task.FromException<WifiChipInfo?>(
                new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));
        }
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_TotalTimeoutHit_ShortCircuitsToChipInfoUnavailable()
    {
        // Closes a Qodo follow-up on PR #199: per-attempt query timeouts
        // compound with retry delays, so a high MaximumAttempts × non-trivial
        // per-attempt latency could block far beyond the configured retry
        // budget while holding _operationLock. The total-timeout cap caps
        // wall-clock time independent of attempt counts.
        //
        // The fake's per-attempt latency is the Task.Delay below; with
        // 200ms latency × 10 max attempts × 100ms retry delay, a naive
        // implementation would block ~2.9s. The 100ms total timeout
        // forces an early ChipInfoUnavailable return after the first
        // attempt's latency exceeds the budget.
        var device = new SlowFakeWifiChipInfoStreamingDevice(
            "COM17",
            attemptLatency: TimeSpan.FromMilliseconds(200));

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 10;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(100);
        options.WifiChipInfoTotalTimeout = TimeSpan.FromMilliseconds(100);

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

    private sealed class SlowFakeWifiChipInfoStreamingDevice : IStreamingDevice, IWifiChipInfoProvider
    {
        private readonly TimeSpan _attemptLatency;
        public SlowFakeWifiChipInfoStreamingDevice(string name, TimeSpan attemptLatency)
        {
            Name = name;
            _attemptLatency = attemptLatency;
            IsConnected = true;
        }
        public string Name { get; }
        public bool IsConnected { get; private set; }
        public DeviceState State => DeviceState.Connected;
        public DeviceMetadata Metadata { get; } = new();
        public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();
        public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();
        public int SampleRateHz { get; set; }
        public bool IsStreaming { get; private set; }
        public event EventHandler<DeviceStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated { add { } remove { } }
        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            return ValueTask.CompletedTask;
        }

        public void Send<T>(IOutboundMessage<T> message) { }
        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IDigitalChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IDigitalChannel channel, bool value) { }
        public void SetPwmEnabled(IDigitalChannel channel, bool enabled) { }
        public void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent) { }
        public void SetPwmFrequency(int frequencyHz) { }
        public int PwmFrequencyHz => 0;
        public void SetAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public int PwmFrequencyFloorHz => 0;
        public void StageAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public void LatchAnalogOutputs() { }
        public Task<double> GetAnalogOutputAsync(IAnalogOutputChannel channel, CancellationToken cancellationToken = default) => Task.FromResult(0.0);
        public bool Supports(DeviceFeature feature) => false;
        public Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshDeviceStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapabilityDocument?> ReadCapabilityDocumentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapabilityDocument?>(null);
        public void Reboot() => IsConnected = false;
        public Task StartStreamingAsync(CancellationToken cancellationToken = default) { StartStreaming(); return Task.CompletedTask; }
        public Task StopStreamingAsync(CancellationToken cancellationToken = default) { StopStreaming(); return Task.CompletedTask; }
        public Task RebootAsync(CancellationToken cancellationToken = default) { Reboot(); return Task.CompletedTask; }

        public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationSlopeAsync(int channelNumber, double slope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationOffsetAsync(int channelNumber, double offset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<WifiChipInfo?> GetWifiChipInfoAsync(CancellationToken cancellationToken = default)
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
            Version = new FirmwareVersion(19, 5, 4, 0, null, 0),
            TagName = "19.5.4",
            IsPreRelease = false
        };
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM16",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        options.WifiChipInfoMaximumAttempts = 3;
        options.WifiChipInfoRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService { LatestWifiRelease = wifiRelease },
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Equal(1, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_PowerOnBeforeProbeEnabled_SendsTurnDeviceOnBeforeChipInfoProbe()
    {
        // Closes #301: right after a PIC32 reflash the WINC comes back powered
        // off, so the probe must power it on before the first GETChipInfo?
        // query, not merely rely on retrying a powered-off module.
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM18",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = true;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await service.CheckWifiFirmwareStatusAsync(device);

        Assert.Contains("SYSTem:POWer:STATe 1", device.SentCommands);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_PowerOnBeforeProbeDisabled_DoesNotSendTurnDeviceOn()
    {
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM19",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = false;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await service.CheckWifiFirmwareStatusAsync(device);

        Assert.DoesNotContain("SYSTem:POWer:STATe 1", device.SentCommands);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_DeviceNotConnected_SkipsPowerOnAndDoesNotThrow()
    {
        // FakeWifiChipInfoStreamingDevice.Send does not itself throw when
        // disconnected (unlike the real DaqifiDevice), so this asserts the
        // guard on the caller side: CheckWifiFirmwareStatusAsync must not
        // attempt to send TurnDeviceOn to a device it knows is disconnected.
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM20",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            },
            isConnected: false);

        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = true;

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var status = await service.CheckWifiFirmwareStatusAsync(device);

        Assert.DoesNotContain("SYSTem:POWer:STATe 1", device.SentCommands);
        Assert.NotNull(status);
    }

    [Fact]
    public async Task CheckWifiFirmwareStatusAsync_PowerOnBeforeProbe_WaitsSettleDelayBeforeProbing()
    {
        var device = new FakeWifiChipInfoStreamingDevice(
            "COM21",
            chipInfo: new WifiChipInfo
            {
                ChipId = 1234,
                FirmwareVersion = "19.5.4",
                BuildDate = "Jan  8 2019"
            });

        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = true;
        options.PowerOnWifiModuleSettleDelay = TimeSpan.FromMilliseconds(200);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        await service.CheckWifiFirmwareStatusAsync(device);

        // Assert against the fake's own recorded event timestamps rather than
        // the whole call's wall-clock duration — this isolates the assertion
        // to the actual gap between the two operations under test instead of
        // being sensitive to unrelated CI/test-framework overhead.
        Assert.NotNull(device.TurnDeviceOnSentAt);
        Assert.NotNull(device.FirstGetWifiChipInfoCallAt);
        Assert.True(
            device.FirstGetWifiChipInfoCallAt > device.TurnDeviceOnSentAt,
            "Expected the chip-info probe to happen after the power-on command.");

        var gap = device.FirstGetWifiChipInfoCallAt!.Value - device.TurnDeviceOnSentAt!.Value;
        Assert.True(gap >= TimeSpan.FromMilliseconds(180),
            $"Expected the probe to wait out the settle delay between power-on and chip-info query, but the gap was only {gap.TotalMilliseconds}ms.");
    }

    // ----- #299: standalone bootloader diagnostics (CheckBootloaderHealthAsync / ResetBootloaderAsync) -----

    private static FirmwareUpdateService CreateDiagnosticsService(
        FakeHidTransport hidTransport,
        FakeHidDeviceEnumerator enumerator,
        FirmwareUpdateServiceOptions? options = null)
    {
        return new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01]]),
            enumerator,
            options ?? CreateFastOptions());
    }

    private static FakeHidDeviceEnumerator SingleBootloaderEnumerator(string devicePath = "path-1") =>
        new([[new HidDeviceInfo(0x04D8, 0x003C, devicePath, "SN-1", "DAQiFi Bootloader")]]);

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenBootloaderHealthy_ReturnsVersionAndDisconnects()
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // valid version response

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());

        var version = await service.CheckBootloaderHealthAsync();

        Assert.Equal("1.0", version);
        // Only the version request was written — no erase/program/jump. And the HID
        // handle is released before returning so a later flow starts clean.
        Assert.Equal(new byte[] { 0x11 }, Assert.Single(hidTransport.Writes));
        Assert.False(hidTransport.IsConnected);
        Assert.Equal(1, hidTransport.DisconnectCalls);
        // A diagnostic is not an update: the update state machine is never driven.
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenTheServiceIsDisposedMidOperation_KeepsItsOutcome()
    {
        // Disposing the service used to dispose the operation lock under the running
        // operation, so its finally's Release threw ObjectDisposedException and replaced the
        // result it had already obtained.
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());
        hidTransport.OnConnect = service.Dispose;

        var version = await service.CheckBootloaderHealthAsync();

        Assert.Equal("1.0", version);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CheckBootloaderHealthAsync());
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenTargetPathProvided_ConnectsByPath()
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator("path-42"));

        await service.CheckBootloaderHealthAsync("path-42");

        Assert.Equal(1, hidTransport.ConnectByPathAttempts);
        Assert.Equal("path-42", hidTransport.LastConnectByPath);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenVersionResponseInvalid_ThrowsFirmwareUpdateExceptionWithConnectingGuidance()
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0xEE]); // FakeBootloaderProtocol decodes 0xEE → "Error"

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.CheckBootloaderHealthAsync());

        Assert.Equal(FirmwareUpdateState.Connecting, ex.FailedState);
        // The phase label is what a recovery dialog shows the operator, so pin it, not
        // just the state: "Connecting" alone cannot distinguish a transport that never
        // opened from one that opened and then answered the version query with garbage.
        Assert.Equal("request bootloader version", ex.Operation);
        Assert.Equal(
            "Bootloader health check failed in state 'Connecting' while request bootloader version.",
            ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.RecoveryGuidance));
        // Failed diagnostics must still leave the transport released and the service Idle.
        Assert.False(hidTransport.IsConnected);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenNoBootloaderEnumerates_ThrowsWithWaitingForBootloaderGuidance()
    {
        var hidTransport = new FakeHidTransport();
        // Enumerator only ever returns an empty list → WaitingForBootloader times out.
        var enumerator = new FakeHidDeviceEnumerator([Array.Empty<HidDeviceInfo>()]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(150);

        var service = CreateDiagnosticsService(hidTransport, enumerator, options);

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.CheckBootloaderHealthAsync());

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, ex.FailedState);
        Assert.Equal("wait for HID bootloader enumeration", ex.Operation);
        Assert.IsType<TimeoutException>(ex.InnerException);
        // #944: a diagnostic touches no flash.
        Assert.Equal(Pic32FlashCondition.NotChanged, ex.FlashCondition);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenTargetPathWhitespace_ThrowsArgumentException()
    {
        var service = CreateDiagnosticsService(new FakeHidTransport(), SingleBootloaderEnumerator());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CheckBootloaderHealthAsync("   "));
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        var service = CreateDiagnosticsService(new FakeHidTransport(), SingleBootloaderEnumerator());
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.CheckBootloaderHealthAsync());
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenBootloaderPresent_WritesJumpToAppAndDisconnects()
    {
        var hidTransport = new FakeHidTransport();

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());

        await service.ResetBootloaderAsync();

        // Exactly the JMP_TO_APP message (0x55 in FakeBootloaderProtocol) — no version
        // read, no erase, no program.
        Assert.Equal(new byte[] { 0x55 }, Assert.Single(hidTransport.Writes));
        Assert.False(hidTransport.IsConnected);
        Assert.Equal(1, hidTransport.DisconnectCalls);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenTargetPathProvided_ConnectsByPath()
    {
        var hidTransport = new FakeHidTransport();

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator("path-9"));

        await service.ResetBootloaderAsync("path-9");

        Assert.Equal(1, hidTransport.ConnectByPathAttempts);
        Assert.Equal("path-9", hidTransport.LastConnectByPath);
        Assert.Equal(new byte[] { 0x55 }, Assert.Single(hidTransport.Writes));
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenNoBootloaderEnumerates_ThrowsFirmwareUpdateException()
    {
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([Array.Empty<HidDeviceInfo>()]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(150);

        var service = CreateDiagnosticsService(hidTransport, enumerator, options);

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.ResetBootloaderAsync());

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, ex.FailedState);
        // Same phase label as the health check's wait step — the two diagnostics share
        // this preamble and must keep reporting it identically.
        Assert.Equal("wait for HID bootloader enumeration", ex.Operation);
        Assert.Empty(hidTransport.Writes);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenTheHidConnectFails_ReportsTheConnectPhase()
    {
        // The bootloader enumerates but the HID handle cannot be opened. Neither
        // diagnostic covered this middle phase, yet it is the one both of them share:
        // the state/operation pair reported here is the only thing distinguishing
        // "no bootloader present" from "bootloader present but unopenable".
        var hidTransport = new FakeHidTransport();
        hidTransport.ConnectFailures.Enqueue(new UnauthorizedAccessException("handle in use"));

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.CheckBootloaderHealthAsync());

        Assert.Equal(FirmwareUpdateState.Connecting, ex.FailedState);
        Assert.Equal("connect HID transport", ex.Operation);
        Assert.Equal(
            "Bootloader health check failed in state 'Connecting' while connect HID transport.",
            ex.Message);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        // A connect that never opened must not have produced a version query.
        Assert.Empty(hidTransport.Writes);
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenTheHidConnectFails_ReportsTheConnectPhase()
    {
        // Symmetry with the health check above: same shared phase, same labels, only
        // the failure subject differs.
        var hidTransport = new FakeHidTransport();
        hidTransport.ConnectFailures.Enqueue(new UnauthorizedAccessException("handle in use"));

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.ResetBootloaderAsync());

        Assert.Equal(FirmwareUpdateState.Connecting, ex.FailedState);
        Assert.Equal("connect HID transport", ex.Operation);
        Assert.Equal(
            "Bootloader soft reset failed in state 'Connecting' while connect HID transport.",
            ex.Message);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        // Nothing may reach the wire when the transport never opened — in particular
        // no JMP_TO_APP.
        Assert.Empty(hidTransport.Writes);
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenTargetPathWhitespace_ThrowsArgumentException()
    {
        var service = CreateDiagnosticsService(new FakeHidTransport(), SingleBootloaderEnumerator());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ResetBootloaderAsync("\t"));
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenJumpToAppWriteHangs_TimesOutInJumpingToAppState()
    {
        // The standalone JMP_TO_APP write must be bounded by JumpingToApplicationTimeout,
        // just like the full update flow's jump step — a write that blocks (or ignores
        // cancellation) must not let the reset run unbounded.
        var hidTransport = new FakeHidTransport
        {
            WriteHook = (_, ct) => Task.Delay(Timeout.Infinite, ct)
        };

        var options = CreateFastOptions();
        options.JumpingToApplicationTimeout = TimeSpan.FromMilliseconds(150);

        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator(), options);

        var ex = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => service.ResetBootloaderAsync());

        Assert.Equal(FirmwareUpdateState.JumpingToApplication, ex.FailedState);
        Assert.Equal("issue JMP_TO_APP soft reset", ex.Operation);
        Assert.IsType<TimeoutException>(ex.InnerException);
        // The hung write never completed, so nothing was recorded, and the HID
        // transport is still disconnected on the way out.
        Assert.Empty(hidTransport.Writes);
        Assert.False(hidTransport.IsConnected);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        // Symmetry with CheckBootloaderHealthAsync's disposed guard.
        var service = CreateDiagnosticsService(new FakeHidTransport(), SingleBootloaderEnumerator());
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.ResetBootloaderAsync());
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_WhenTokenAlreadyCanceled_ThrowsOperationCanceled()
    {
        var service = CreateDiagnosticsService(new FakeHidTransport(), SingleBootloaderEnumerator());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckBootloaderHealthAsync(cancellationToken: cts.Token));

        // A canceled diagnostic must not strand the service in a non-idle state.
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task ResetBootloaderAsync_WhenTokenAlreadyCanceled_ThrowsOperationCanceled()
    {
        var hidTransport = new FakeHidTransport();
        var service = CreateDiagnosticsService(hidTransport, SingleBootloaderEnumerator());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ResetBootloaderAsync(cancellationToken: cts.Token));

        // Nothing may reach the wire on a pre-canceled reset.
        Assert.Empty(hidTransport.Writes);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_ReentrantCallFromUpdateProgressCallback_ThrowsInsteadOfDeadlocking()
    {
        // A diagnostic owns the HID connect/version exchange, so unlike the read-only
        // CheckWifiFirmwareStatusAsync probe it must NOT be allowed through from inside
        // an in-flight update's synchronous callback. It must fail fast with
        // InvalidOperationException rather than deadlock on the non-reentrant lock.
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(150);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0xA1, 0x01]]),
            new FakeHidDeviceEnumerator([Array.Empty<HidDeviceInfo>()]),
            options);

        Exception? reentrantFailure = null;
        var reentryAttempted = false;

        var progress = new SyncProgress<FirmwareUpdateProgress>(_ =>
        {
            if (reentryAttempted)
            {
                return;
            }
            reentryAttempted = true;
            reentrantFailure = Record.ExceptionAsync(
                () => service.CheckBootloaderHealthAsync()).GetAwaiter().GetResult();
        });

        var hexPath = CreateTempFile();
        try
        {
            // Hard timeout so a regression of the guard fails fast instead of hanging
            // the run on the non-reentrant _operationLock. The first progress report
            // fires before UpdateFirmwareAsync returns its task, so the update runs off
            // the test thread: called inline, a deadlock would block here before the
            // timeout was ever attached. The timeout surfaces as a cancellation that
            // ThrowsAny<Exception> would accept, so it is recorded and rejected by token.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var outerFailure = await Record.ExceptionAsync(
                () => Task.Run(() => service.UpdateFirmwareAsync(device, hexPath, progress: progress))
                    .WaitAsync(timeoutCts.Token));

            Assert.False(
                outerFailure is OperationCanceledException canceled
                    && canceled.CancellationToken == timeoutCts.Token,
                "UpdateFirmwareAsync hit the test's own timeout — reentry deadlocked on the operation lock instead of throwing.");
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.True(reentryAttempted, "Progress callback never fired — test setup wrong.");

        // The callback records the reentrant call's own exception, so it never escapes
        // into the update: the reentry failure is asserted where it happened.
        var failure = Assert.IsType<InvalidOperationException>(reentrantFailure);
        Assert.Contains("in-flight firmware operation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBootloaderHealthAsync_ConcurrentCallFromSeparateContext_WaitsInsteadOfThrowing()
    {
        // The documented contract: only callback reentrancy is rejected. A concurrent
        // call from a SEPARATE execution context must serialize on the shared lock and
        // then proceed — it must not throw InvalidOperationException.
        //
        // Both facts are read off the code's own structure, not off a clock (issue #803).
        //
        // "The second call was not rejected" is observable the instant
        // CheckBootloaderHealthAsync returns its task. RunBootloaderDiagnosticAsync checks
        // disposal, the target-path argument and the _isInsideOperation reentrancy flag
        // synchronously, all before its first await — so a rejected call hands back an
        // already-faulted task. The first await is _operationLock.WaitAsync, and a
        // SemaphoreSlim with no count left never completes that synchronously. An
        // incomplete task therefore means the call passed the guard and is queued on the
        // lock, which is exactly the fact asserted, with no window to wait out.
        //
        // "The two calls did not interleave" is read off the recorded order of the HID
        // writes. Each health check writes once, so a second write logged before the first
        // call's write returned would be an interleave.
        //
        // The previous version waited out a 250 ms Task.Delay to let the second call "settle"
        // on the lock. That delay ran inside a real production budget: the first call is
        // parked in its version request, which Pic32FirmwareUpdater runs under
        // ExecuteWithStateTimeoutAsync for the Connecting state, and CreateFastOptions sets
        // ConnectingTimeout to 2 s. The 250 ms sleep plus thread-pool dispatch latency on a
        // loaded Windows net9 runner spent that whole 2 s budget, so the first call timed out
        // and `await first` threw instead of returning the version. Nothing is waited out now,
        // so the budget is no longer part of the test.
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        // Write order, under a lock because an interleave is the failure this records:
        // unsynchronized bookkeeping would itself be a data race in the case being detected.
        var writeOrder = new List<string>();
        var writeOrderLock = new object();
        void RecordWrite(string label)
        {
            lock (writeOrderLock)
            {
                writeOrder.Add(label);
            }
        }

        var firstWriteClaimed = 0;
        hidTransport.WriteHook = async (_, _) =>
        {
            if (Interlocked.Exchange(ref firstWriteClaimed, 1) != 0)
            {
                RecordWrite("second-write");
                return;
            }

            RecordWrite("first-write-entered");
            firstCallHoldsLock.TrySetResult();
            await releaseFirst.Task;
            RecordWrite("first-write-returned");
        };

        var enumerator = new FakeHidDeviceEnumerator([
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")],
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var service = CreateDiagnosticsService(hidTransport, enumerator);

        var first = service.CheckBootloaderHealthAsync();
        await firstCallHoldsLock.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = service.CheckBootloaderHealthAsync();

        // The guard runs synchronously, so a rejected second call is already faulted here.
        Assert.False(
            second.IsCompleted,
            "Second diagnostic completed before the first released the operation lock — "
            + "the reentrancy guard rejected a call from a separate context.");

        releaseFirst.SetResult();

        Assert.Equal("1.0", await first);
        Assert.Equal("1.0", await second);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);

        // Both calls reached the wire, and the second one did so only after the first was
        // done with it. Awaiting both above is what makes this non-vacuous: a second call
        // that never ran would leave "second-write" out of the list.
        List<string> recorded;
        lock (writeOrderLock)
        {
            recorded = [.. writeOrder];
        }

        Assert.Equal(
            ["first-write-entered", "first-write-returned", "second-write"],
            recorded);
    }

    [Fact]
    public async Task BootloaderDiagnostic_AfterHealthCheck_ServiceStaysUsableForAnotherDiagnostic()
    {
        // A successful diagnostic must leave CurrentState Idle so a subsequent
        // diagnostic (or a real update) is not blocked by a stale state.
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var enumerator = new FakeHidDeviceEnumerator([
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")],
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var service = CreateDiagnosticsService(hidTransport, enumerator);

        var first = await service.CheckBootloaderHealthAsync();
        var second = await service.CheckBootloaderHealthAsync();

        Assert.Equal("1.0", first);
        Assert.Equal("1.0", second);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
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
            PostWifiFirmwareModeDelay = TimeSpan.FromMilliseconds(5),
            PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5),
            HidConnectRetryDelay = TimeSpan.FromMilliseconds(5),
            FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5),
            WifiProcessTimeout = TimeSpan.FromSeconds(2),
            // Collapse the WiFi-flash lifecycle waits for unit tests: the fake transport
            // releases its port synchronously and the fake process runner needs no
            // prompt/bridge-init window, so these delays are pure latency here.
            PostWifiDisconnectPortReleaseDelay = TimeSpan.Zero,
            PostWifiBridgeEntryDelay = TimeSpan.Zero,
            WifiBootPromptResponseDelay = TimeSpan.Zero,
            WifiFlashRetryDelay = TimeSpan.FromMilliseconds(5),
            // Disable the macOS USB CDC stale-handle settling delay for unit
            // tests — FakeStreamingDevice doesn't reproduce the re-enum race,
            // so the dance is pure latency that would blow the fast budgets.
            PostReconnectStaleHandleDelay = TimeSpan.Zero,
            // Skip the WINC power-on settle wait — fakes don't model power state,
            // so the delay is pure latency here. Individual tests re-enable it
            // where the power-on behavior itself is under test.
            PowerOnWifiModuleSettleDelay = TimeSpan.Zero,
            // Same reasoning for the transparent-mode exit settle: the fake device has no
            // bridge to leave. Tests that exercise the wait itself set it explicitly.
            PostUsbTransparentModeExitDelay = TimeSpan.Zero
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

    private sealed class FakeWifiChipInfoStreamingDevice : IStreamingDevice, IWifiChipInfoProvider, ISerialPortNamed
    {
        private readonly WifiChipInfo? _chipInfo;
        private DeviceState _state = DeviceState.Connected;
        private int _remainingTransientFailures;
        private int _remainingWifiNotInitializedFailures;

        public FakeWifiChipInfoStreamingDevice(
            string name,
            WifiChipInfo? chipInfo,
            int transientFailuresBeforeSuccess = 0,
            bool isConnected = true,
            int wifiNotInitializedFailuresBeforeSuccess = 0,
            string? serialPortName = null)
        {
            Name = name;
            // Defaults to the name, as these tests name the double after the port it sits on.
            // The port-resolution tests in WifiModuleUpdaterTests set the two apart (issue #842).
            SerialPortName = serialPortName ?? name;
            _chipInfo = chipInfo;
            _remainingTransientFailures = transientFailuresBeforeSuccess;
            _remainingWifiNotInitializedFailures = wifiNotInitializedFailuresBeforeSuccess;
            IsConnected = isConnected;
            if (!isConnected)
            {
                _state = DeviceState.Disconnected;
            }
        }

        /// <summary>Number of times <c>SYSTem:COMMunicate:LAN:APPLY</c> was sent.</summary>
        public int LanApplySentCount { get; private set; }

        private readonly System.Diagnostics.Stopwatch _eventStopwatch = System.Diagnostics.Stopwatch.StartNew();

        public int GetWifiChipInfoCallCount { get; private set; }

        public string Name { get; }

        /// <inheritdoc />
        public string? SerialPortName { get; set; }

        public bool IsConnected { get; private set; }
        public DeviceState State => _state;
        public DeviceMetadata Metadata { get; } = new();
        public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();
        public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();
        public int SampleRateHz { get; set; }
        public bool IsStreaming { get; private set; }

        public List<string> SentCommands { get; } = [];

        /// <summary>Elapsed time (from construction) at which "SYSTem:POWer:STATe 1" was sent, if any.</summary>
        public TimeSpan? TurnDeviceOnSentAt { get; private set; }

        /// <summary>Elapsed time (from construction) at which the first <see cref="GetWifiChipInfoAsync"/> call landed.</summary>
        public TimeSpan? FirstGetWifiChipInfoCallAt { get; private set; }

        public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated
        {
            add { }
            remove { }
        }

        public void Connect()
        {
            IsConnected = true;
            var previous = _state;
            _state = DeviceState.Connected;
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(previous, _state));
        }

        public void Disconnect()
        {
            IsConnected = false;
            var previous = _state;
            _state = DeviceState.Disconnected;
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(previous, _state));
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            return ValueTask.CompletedTask;
        }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> textMessage)
            {
                SentCommands.Add(textMessage.Data);
                if (textMessage.Data == "SYSTem:POWer:STATe 1")
                {
                    TurnDeviceOnSentAt = _eventStopwatch.Elapsed;
                }
                else if (textMessage.Data == "SYSTem:COMMunicate:LAN:APPLY")
                {
                    LanApplySentCount++;
                }
            }
        }

        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IDigitalChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IDigitalChannel channel, bool value) { }
        public void SetPwmEnabled(IDigitalChannel channel, bool enabled) { }
        public void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent) { }
        public void SetPwmFrequency(int frequencyHz) { }
        public int PwmFrequencyHz => 0;
        public void SetAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public int PwmFrequencyFloorHz => 0;
        public void StageAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public void LatchAnalogOutputs() { }
        public Task<double> GetAnalogOutputAsync(IAnalogOutputChannel channel, CancellationToken cancellationToken = default) => Task.FromResult(0.0);
        public bool Supports(DeviceFeature feature) => false;
        public Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshDeviceStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapabilityDocument?> ReadCapabilityDocumentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapabilityDocument?>(null);
        public void Reboot() => Disconnect();
        public Task StartStreamingAsync(CancellationToken cancellationToken = default) { StartStreaming(); return Task.CompletedTask; }
        public Task StopStreamingAsync(CancellationToken cancellationToken = default) { StopStreaming(); return Task.CompletedTask; }
        public Task RebootAsync(CancellationToken cancellationToken = default) { Reboot(); return Task.CompletedTask; }

        public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationSlopeAsync(int channelNumber, double slope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationOffsetAsync(int channelNumber, double offset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WifiChipInfo?> GetWifiChipInfoAsync(CancellationToken cancellationToken = default)
        {
            FirstGetWifiChipInfoCallAt ??= _eventStopwatch.Elapsed;
            GetWifiChipInfoCallCount++;
            if (_remainingTransientFailures > 0)
            {
                _remainingTransientFailures--;
                // Faulted task (not sync throw) more accurately simulates how
                // a real async method surfaces failure — caller's await sees a
                // genuinely-async exception path.
                return Task.FromException<WifiChipInfo?>(
                    new InvalidOperationException("Simulated transient post-reboot failure."));
            }
            if (_remainingWifiNotInitializedFailures > 0)
            {
                _remainingWifiNotInitializedFailures--;
                return Task.FromException<WifiChipInfo?>(
                    new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));
            }
            return Task.FromResult(_chipInfo);
        }
    }
}
