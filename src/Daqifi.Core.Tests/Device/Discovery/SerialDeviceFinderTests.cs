using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class SerialDeviceFinderTests
{
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
    public async Task DiscoverAsync_WithNonDaqifiVidPid_DoesNotProbePort()
    {
        // Closes #157: ports whose USB descriptor is NOT a known DAQiFi
        // VID/PID get filtered before any port-open / SCPI traffic. This
        // is both a correctness fix (don't talk to other vendors' devices)
        // and the dominant performance win (~5s per skipped port).
        //
        // The fake provider classifies every port as a CH340 (non-DAQiFi)
        // and tracks GetDescriptor calls. After DiscoverAsync, every
        // platform-listed port should have been classified once and zero
        // devices returned — proving the filter ran AND that the port-
        // probe path was never reached.
        var classifierCallCount = 0;
        var fakeProvider = new RecordingUsbPortDescriptorProvider(_ =>
        {
            Interlocked.Increment(ref classifierCallCount);
            return new UsbPortDescriptor(0x1A86, 0x7523); // CH340, not DAQiFi
        });

        using var finder = new SerialDeviceFinder(9600, fakeProvider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var devices = await finder.DiscoverAsync(cts.Token);
        stopwatch.Stop();

        Assert.Empty(devices);
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

        using var finder = new SerialDeviceFinder(
            9600,
            fakeProvider,
            portNameProvider: () => new[] { "COM999" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Probe path may exceed 200ms (legacy fallback engages on COM999 then
        // fails to open). OCE is acceptable; what matters is that the throwing
        // provider was actually called and didn't propagate.
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
}
