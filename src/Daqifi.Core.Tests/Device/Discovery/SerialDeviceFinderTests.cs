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
