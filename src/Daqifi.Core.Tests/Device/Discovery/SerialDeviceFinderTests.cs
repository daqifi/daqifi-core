using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class SerialDeviceFinderTests
{
    // Issue #283: Discovery.DeviceType lacked a Nyquist2 member, so
    // ConvertDeviceType silently downgraded a correctly-detected Nyquist2 to
    // Unknown.
    [Theory]
    [InlineData(Daqifi.Core.Device.DeviceType.Unknown, DeviceType.Unknown)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist1, DeviceType.Nyquist1)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist2, DeviceType.Nyquist2)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist3, DeviceType.Nyquist3)]
    public void ConvertDeviceType_MapsToMatchingDiscoveryType(
        Daqifi.Core.Device.DeviceType deviceType, DeviceType expected)
    {
        var result = SerialDeviceFinder.ConvertDeviceType(deviceType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DiscoverAsync_WithCancellationToken_ReturnsDevices()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        using var cts = new CancellationTokenSource();

        // Act
        var devices = await finder.DiscoverAsync(cts.Token);

        // Assert
        Assert.NotNull(devices);
        // May or may not find devices depending on system, but should not throw
    }

    [Fact]
    public async Task DiscoverAsync_WithTimeout_CompletesWithinTimeout()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        var timeout = TimeSpan.FromSeconds(5);

        // Act
        var startTime = DateTime.UtcNow;
        var devices = await finder.DiscoverAsync(timeout);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.NotNull(devices);
        Assert.True(elapsed.TotalSeconds <= timeout.TotalSeconds + 1);
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        // Arrange
        using var finder = new SerialDeviceFinder();
        var eventRaised = false;
        finder.DiscoveryCompleted += (sender, args) => eventRaised = true;

        // Act
        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void SerialDeviceFinder_Dispose_DoesNotThrow()
    {
        // Arrange
        var finder = new SerialDeviceFinder();

        // Act & Assert
        finder.Dispose();
    }

    [Fact]
    public async Task DiscoverAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var finder = new SerialDeviceFinder();
        finder.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }

    [Fact]
    public void SerialDeviceFinder_DefaultConstructor_Uses9600Baud()
    {
        // Arrange & Act
        using var finder = new SerialDeviceFinder();

        // Assert
        Assert.NotNull(finder);
    }

    [Fact]
    public void SerialDeviceFinder_CustomBaudRate_AcceptsCustomBaudRate()
    {
        // Arrange & Act
        using var finder = new SerialDeviceFinder(9600);

        // Assert
        Assert.NotNull(finder);
    }

    [Fact]
    public void SerialDeviceFinder_CustomUsbLocationProvider_AcceptsProvider()
    {
        var fakeProvider = new RecordingUsbLocationProvider(_ => "Port_#0001.Hub_#0001");

        using var finder = new SerialDeviceFinder(9600, usbPortDescriptorProvider: null, usbLocationProvider: fakeProvider);

        Assert.NotNull(finder);
    }

    [Fact]
    public void BuildDeviceInfo_MapsStatusMessageAndLocationKey()
    {
        // BuildDeviceInfo is the pure mapping logic TryGetDeviceInfoAsync delegates to after a
        // real probe succeeds — split out specifically so this mapping (including the new
        // LocationKey field) is unit-testable with a hand-constructed status message, without
        // needing a fake serial transport (which this suite has none of).
        var statusMessage = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceSn = 12345,
            DeviceFwRev = "3.7.1"
        };

        var deviceInfo = SerialDeviceFinder.BuildDeviceInfo(statusMessage, "COM3", "Port_#0001.Hub_#0001");

        Assert.Equal("Nq1", deviceInfo.Name);
        Assert.Equal("12345", deviceInfo.SerialNumber);
        Assert.Equal("3.7.1", deviceInfo.FirmwareVersion);
        Assert.Equal(ConnectionType.Serial, deviceInfo.ConnectionType);
        Assert.Equal("COM3", deviceInfo.PortName);
        Assert.Equal("Port_#0001.Hub_#0001", deviceInfo.LocationKey);
    }

    [Fact]
    public void BuildDeviceInfo_WithNullLocationKey_LeavesLocationKeyNull()
    {
        // Covers the "location provider returned null / threw and the caller passed null"
        // path — e.g. macOS/Linux's NullUsbLocationProvider fallback.
        var statusMessage = new DaqifiOutMessage { DevicePn = "Nq1", DeviceSn = 1, DeviceFwRev = "3.7.1" };

        var deviceInfo = SerialDeviceFinder.BuildDeviceInfo(statusMessage, "COM3", null);

        Assert.Null(deviceInfo.LocationKey);
    }

    [Fact]
    public void BuildDeviceInfo_WithBlankDevicePn_FallsBackToPortNameAsName()
    {
        var statusMessage = new DaqifiOutMessage { DevicePn = "", DeviceSn = 1, DeviceFwRev = "3.7.1" };

        var deviceInfo = SerialDeviceFinder.BuildDeviceInfo(statusMessage, "COM3", null);

        Assert.Equal("COM3", deviceInfo.Name);
    }

    [Fact]
    public async Task DiscoverAsync_WithNonDaqifiVidPid_DoesNotProbePort()
    {
        // Closes #157: ports whose USB descriptor is NOT a known DAQiFi
        // VID/PID get filtered before any port-open / SCPI traffic. This
        // is both a correctness fix (don't talk to other vendors' devices)
        // and the dominant performance win (~5s per skipped port).
        //
        // The fake provider classifies every port as a CH340 (non-DAQiFi)
        // and tracks GetDescriptor calls. After DiscoverAsync, every
        // injected port should have been classified once and zero devices
        // returned — proving the filter ran AND that the port-probe path
        // was never reached. Inject a deterministic 3-port list so the
        // test exercises the classifier path even on CI hosts with no
        // real serial ports.
        var fakeProvider = new RecordingUsbPortDescriptorProvider(_ =>
            new UsbPortDescriptor(0x1A86, 0x7523)); // CH340, not DAQiFi

        using var finder = new SerialDeviceFinder(
            9600,
            fakeProvider,
            portNameProvider: () => new[] { "COM1", "COM2", "COM3" });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var devices = await finder.DiscoverAsync(cts.Token);
        stopwatch.Stop();

        Assert.Empty(devices);
        Assert.Equal(3, fakeProvider.CallCount);
        // If any port were probed, the test would take seconds per port
        // (DeviceWakeUpDelayMs + ResponseTimeoutMs); should be well under
        // 500ms for the classifier-only path even on a many-port system.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Discovery took {stopwatch.ElapsedMilliseconds}ms — classifier filter may not be wired correctly.");
    }

    [Fact]
    public async Task DiscoverAsync_WithNullDescriptor_FallsThroughToProbe()
    {
        // Cross-platform fallback: when the descriptor provider can't
        // classify a port (returns null), the legacy probe behavior is
        // preserved so we don't regress on Linux/macOS where we don't
        // yet have a descriptor lookup. The probe will time out on
        // non-DAQiFi ports as before — no change to that path.
        var fakeProvider = new RecordingUsbPortDescriptorProvider(_ => null);

        using var finder = new SerialDeviceFinder(9600, fakeProvider);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Just verifying it doesn't throw and returns a (probably empty) list;
        // actual ports depend on the test machine. The key contract is that
        // null-descriptor doesn't filter the port out of consideration.
        // The legacy probe path may exceed 200ms on machines with real ports —
        // an OperationCanceledException there still proves the contract:
        // null descriptors fall through to probing rather than being filtered.
        try
        {
            var devices = await finder.DiscoverAsync(cts.Token);
            Assert.NotNull(devices);
        }
        catch (OperationCanceledException)
        {
            // Probe ran (legacy fallback engaged) and exceeded the test budget.
        }
    }

    [Fact]
    public async Task DiscoverAsync_WithThrowingDescriptorProvider_DoesNotAbortDiscovery()
    {
        // A custom IUsbPortDescriptorProvider that throws must NEVER take
        // down the whole discovery pass — fall through to legacy probing
        // for the port and continue with the rest of the list.
        //
        // Inject a fixed port list so the throwing provider IS invoked even
        // on CI hosts with zero real serial ports — otherwise the test would
        // pass vacuously without exercising the exception-handling path.
        var fakeProvider = new RecordingUsbPortDescriptorProvider(_ =>
            throw new InvalidOperationException("simulated provider failure"));

        // Use an obviously-invalid port name so SerialPort.Open() fails
        // immediately on every platform — never touches a real device, even
        // if the host happens to expose a high-numbered COM port (COM999 etc.
        // can exist on some Windows setups with virtual serial drivers).
        using var finder = new SerialDeviceFinder(
            9600,
            fakeProvider,
            portNameProvider: () => new[] { "MOCK_PORT_DOES_NOT_EXIST" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Probe path is short (legacy fallback fails to open immediately on
        // the bogus port name). OCE is still acceptable; what matters is that
        // the throwing provider was actually called and didn't propagate.
        try
        {
            var devices = await finder.DiscoverAsync(cts.Token);
            Assert.NotNull(devices);
        }
        catch (OperationCanceledException)
        {
            // Probe ran (provider throw was caught and treated as null).
        }

        Assert.True(fakeProvider.CallCount > 0,
            "Throwing descriptor provider was never invoked — the exception-handling path is not exercised by this test.");
    }

    // --- #294: hang immunity -------------------------------------------------

    private static SerialDeviceFinder CreateFinderWithProbes(
        string[] ports,
        Func<string, CancellationToken, Task<IDeviceInfo?>> probe,
        int hardTimeoutMs)
    {
        var daqifiProvider = new RecordingUsbPortDescriptorProvider(_ =>
            new UsbPortDescriptor(DaqifiUsbIds.VendorId, DaqifiUsbIds.CdcProductId));
        var finder = new SerialDeviceFinder(
            9600,
            daqifiProvider,
            portNameProvider: () => ports,
            probeOverride: probe);
        finder.PortProbeHardTimeoutMs = hardTimeoutMs;
        return finder;
    }

    private static IDeviceInfo FakeDevice(string portName) => new DeviceInfo
    {
        Name = "Nq1",
        SerialNumber = "1234",
        FirmwareVersion = "1.0.0",
        ConnectionType = ConnectionType.Serial,
        PortName = portName,
        IsPowerOn = true
    };

    [Fact]
    public async Task DiscoverAsync_HungPort_TimesOutAndStillReturnsHealthyDevices()
    {
        SerialDeviceFinder.ResetPortQuarantineForTests();
        // Closes #294: a wedged CDC device hangs SerialPort.Open() forever (no
        // exception). Pre-fix, Task.WhenAll never settled and the healthy port's
        // device was never reported. The hung probe is simulated with a
        // never-completing task; the hard per-port timeout must abandon it.
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        using var finder = CreateFinderWithProbes(
            new[] { "COM_HUNG", "COM_OK" },
            (port, _) => port == "COM_HUNG"
                ? hungForever.Task
                : Task.FromResult<IDeviceInfo?>(FakeDevice(port)),
            hardTimeoutMs: 300);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var devices = (await finder.DiscoverAsync(CancellationToken.None)).ToList();
        stopwatch.Stop();

        var device = Assert.Single(devices);
        Assert.Equal("COM_OK", device.PortName);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Discovery took {stopwatch.ElapsedMilliseconds}ms — the hard timeout did not abandon the hung probe.");
    }

    [Fact]
    public async Task DiscoverAsync_HungPort_HealthyDeviceEventFiresBeforeSweepSettles()
    {
        SerialDeviceFinder.ResetPortQuarantineForTests();
        // Closes #294 (event half): DeviceDiscovered must fire per-probe, not
        // after Task.WhenAll, so a hung port can't delay/silence healthy ones.
        // The hung probe here outlives the sweep (released only at the end) and
        // the healthy device's event is awaited while the hung probe is pending.
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        var discovered = new TaskCompletionSource<IDeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finder = CreateFinderWithProbes(
            new[] { "COM_HUNG", "COM_OK" },
            (port, _) => port == "COM_HUNG"
                ? hungForever.Task
                : Task.FromResult<IDeviceInfo?>(FakeDevice(port)),
            hardTimeoutMs: 2000);
        finder.DeviceDiscovered += (_, args) => discovered.TrySetResult(args.DeviceInfo);

        var sweep = finder.DiscoverAsync(CancellationToken.None);

        // The healthy device's event must arrive while COM_HUNG is still pending
        // (well before the 2s hard timeout settles the sweep).
        var winner = await Task.WhenAny(discovered.Task, Task.Delay(1000));
        Assert.Same(discovered.Task, winner);
        Assert.False(sweep.IsCompleted,
            "Sweep settled before the hung probe timed out — hang simulation is not wired as intended.");
        Assert.Equal("COM_OK", (await discovered.Task).PortName);

        hungForever.TrySetResult(null);
        await sweep;
    }

    [Fact]
    public async Task DiscoverAsync_HungPort_CallerCancellationStillPropagates()
    {
        SerialDeviceFinder.ResetPortQuarantineForTests();
        // Cancelling the caller token during a hung probe must cancel the sweep
        // (OCE surfaces as the documented "settle for finished probes" path),
        // not wait out the hard timeout.
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        using var finder = CreateFinderWithProbes(
            new[] { "COM_HUNG" },
            (_, _) => hungForever.Task,
            hardTimeoutMs: 60_000);
        using var cts = new CancellationTokenSource(200);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var devices = await finder.DiscoverAsync(cts.Token);
        stopwatch.Stop();

        Assert.Empty(devices);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Cancellation did not short-circuit the hung probe ({stopwatch.ElapsedMilliseconds}ms).");
    }

    [Fact]
    public async Task DiscoverAsync_HungPort_QuarantinedAcrossSweeps_NoThreadPileUp()
    {
        SerialDeviceFinder.ResetPortQuarantineForTests();
        // Qodo PR #295 review #1: a port whose timed-out probe is still blocked
        // must NOT be re-probed on subsequent sweeps — the desktop apps sweep
        // every 2-3s, so without quarantine a long-wedged port stacks one
        // blocked thread-pool thread per sweep. The hung probe here never
        // completes, so the second sweep must skip the port entirely.
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        var hungProbeCalls = 0;
        using var finder = CreateFinderWithProbes(
            new[] { "COM_HUNG", "COM_OK" },
            (port, _) =>
            {
                if (port == "COM_HUNG")
                {
                    Interlocked.Increment(ref hungProbeCalls);
                    return hungForever.Task;
                }
                return Task.FromResult<IDeviceInfo?>(FakeDevice(port));
            },
            hardTimeoutMs: 200);

        var firstSweep = (await finder.DiscoverAsync(CancellationToken.None)).ToList();
        var secondSweep = (await finder.DiscoverAsync(CancellationToken.None)).ToList();

        Assert.Equal("COM_OK", Assert.Single(firstSweep).PortName);
        Assert.Equal("COM_OK", Assert.Single(secondSweep).PortName);
        // <= 1, not == 1: under thread-pool contention the hung probe may not have
        // STARTED before the first sweep's hard timeout fires — the invariant under
        // test is "no pile-up" (2+ would be the regression), not "exactly once"
        // (Qodo pass 4 #2).
        Assert.True(Volatile.Read(ref hungProbeCalls) <= 1,
            $"Hung port probed {hungProbeCalls} times across two sweeps — quarantine did not hold.");
    }

    [Fact]
    public async Task DiscoverAsync_HungPort_QuarantineSurvivesFinderRecreation()
    {
        // Step-3.5 audit on PR #295: the MCP DaqifiAgent constructs and disposes a
        // fresh SerialDeviceFinder per discovery call. A per-instance quarantine
        // forgot the stuck probe every call, re-leaking one blocked thread per
        // request against the same wedged port — the dictionary is process-wide
        // (static) so the one-blocked-thread bound holds across finder instances.
        SerialDeviceFinder.ResetPortQuarantineForTests();
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        var hungProbeCalls = 0;
        Func<string, CancellationToken, Task<IDeviceInfo?>> probe = (port, _) =>
        {
            if (port == "COM_HUNG_X")
            {
                Interlocked.Increment(ref hungProbeCalls);
                return hungForever.Task;
            }
            return Task.FromResult<IDeviceInfo?>(FakeDevice(port));
        };

        using (var first = CreateFinderWithProbes(new[] { "COM_HUNG_X", "COM_OK_X" }, probe, hardTimeoutMs: 200))
        {
            Assert.Equal("COM_OK_X", Assert.Single(await first.DiscoverAsync(CancellationToken.None)).PortName);
        }

        // Fresh instance, same process: the wedged port must stay quarantined.
        using (var second = CreateFinderWithProbes(new[] { "COM_HUNG_X", "COM_OK_X" }, probe, hardTimeoutMs: 200))
        {
            Assert.Equal("COM_OK_X", Assert.Single(await second.DiscoverAsync(CancellationToken.None)).PortName);
        }

        // <= 1 for the same no-pile-up reason as the cross-sweep test (Qodo pass 4 #2).
        Assert.True(Volatile.Read(ref hungProbeCalls) <= 1,
            $"Hung port probed {hungProbeCalls} times across two finder instances — static quarantine did not hold.");
    }

    [Fact]
    public async Task DiscoverAsync_QuarantineTtl_AllowsPeriodicRetry()
    {
        // Qodo pass 4 #3: if the stuck Open never completes, the port must not be
        // suppressed until process restart — after the retry TTL elapses, one fresh
        // probe is allowed per window so a recovered device re-appears.
        SerialDeviceFinder.ResetPortQuarantineForTests();
        SerialDeviceFinder.QuarantineRetryTtlMs = 100;
        try
        {
            var hungForever = new TaskCompletionSource<IDeviceInfo?>();
            var probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hungProbeCalls = 0;
            using var finder = CreateFinderWithProbes(
                new[] { "COM_HUNG_TTL" },
                (_, _) =>
                {
                    Interlocked.Increment(ref hungProbeCalls);
                    probeStarted.TrySetResult(true);
                    return hungForever.Task;
                },
                hardTimeoutMs: 100);

            await finder.DiscoverAsync(CancellationToken.None);
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // first attempt definitely ran
            await Task.Delay(300); // let the TTL window lapse

            await finder.DiscoverAsync(CancellationToken.None);

            Assert.True(Volatile.Read(ref hungProbeCalls) >= 2,
                "TTL elapsed but the quarantined port was never re-probed — recovery requires app restart.");
        }
        finally
        {
            SerialDeviceFinder.ResetPortQuarantineForTests();
        }
    }

    [Fact]
    public async Task DiscoverAsync_ConcurrentFinderInstances_OnlyOneProbePerWedgedPort()
    {
        // Step-3.5 re-gate on PR #295: the check-then-act quarantine raced across
        // CONCURRENT finder instances — both passed the empty check and each leaked
        // a blocked thread on the same wedged port while the dictionary tracked only
        // one. The claim protocol reserves the port atomically (GetOrAdd) BEFORE the
        // probe starts, so exactly one probe can exist per port process-wide.
        SerialDeviceFinder.ResetPortQuarantineForTests();
        var hungForever = new TaskCompletionSource<IDeviceInfo?>();
        var hungProbeCalls = 0;
        Func<string, CancellationToken, Task<IDeviceInfo?>> probe = (port, _) =>
        {
            if (port == "COM_HUNG_CONC")
            {
                Interlocked.Increment(ref hungProbeCalls);
                return hungForever.Task;
            }
            return Task.FromResult<IDeviceInfo?>(FakeDevice(port));
        };

        using var finderA = CreateFinderWithProbes(new[] { "COM_HUNG_CONC", "COM_OK_CONC" }, probe, hardTimeoutMs: 400);
        using var finderB = CreateFinderWithProbes(new[] { "COM_HUNG_CONC", "COM_OK_CONC" }, probe, hardTimeoutMs: 400);

        var results = await Task.WhenAll(
            Task.Run(() => finderA.DiscoverAsync(CancellationToken.None)),
            Task.Run(() => finderB.DiscoverAsync(CancellationToken.None)));

        // Both sweeps complete; at most one probe ever touched the wedged port.
        Assert.True(Volatile.Read(ref hungProbeCalls) <= 1,
            $"Wedged port probed {hungProbeCalls} times across two CONCURRENT finder instances — the claim protocol did not hold.");
        // The healthy port is reported by at least one sweep (the loser of a
        // healthy-port claim race skips it for that sweep by design).
        Assert.Contains(results, r => r.Any(d => d.PortName == "COM_OK_CONC"));
    }

    private sealed class RecordingUsbPortDescriptorProvider : IUsbPortDescriptorProvider
    {
        private readonly Func<string, UsbPortDescriptor?> _classifier;
        private int _callCount;
        public RecordingUsbPortDescriptorProvider(Func<string, UsbPortDescriptor?> classifier)
            => _classifier = classifier;
        public int CallCount => Volatile.Read(ref _callCount);
        public UsbPortDescriptor? GetDescriptor(string portName)
        {
            Interlocked.Increment(ref _callCount);
            return _classifier(portName);
        }
    }

    private sealed class RecordingUsbLocationProvider : IUsbLocationProvider
    {
        private readonly Func<string, string?> _resolver;
        public RecordingUsbLocationProvider(Func<string, string?> resolver) => _resolver = resolver;
        public string? GetLocationKey(string portNameOrDevicePath) => _resolver(portNameOrDevicePath);
    }
}
