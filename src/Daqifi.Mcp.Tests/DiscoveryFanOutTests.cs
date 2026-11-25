using System.Diagnostics;
using System.Net;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Covers <c>discover_devices</c>' transport fan-out (#488): the enabled transports run
/// concurrently, so a pass costs the slower transport's window rather than the sum of both.
/// </summary>
public class DiscoveryFanOutTests
{
    /// <summary>
    /// How long a rendezvous waits before concluding the other transport is never going to start.
    /// Only reached when the fan-out has regressed to sequential, where the first finder blocks
    /// forever waiting for a second that cannot start until it returns.
    /// </summary>
    private const int RendezvousTimeoutMs = 10_000;

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task DiscoverAcrossTransports_RunsTheTransportsConcurrently()
    {
        // Deliberately not a wall-clock assertion. Each finder blocks until the other has entered
        // its own discovery, so "both got in at once" is proven by the pass completing at all:
        // run sequentially, the first finder waits out RendezvousTimeoutMs on a partner that has
        // not been started yet, and the assertions below fail.
        var rendezvous = new Rendezvous(participants: 2);
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.Wifi) }, rendezvous: rendezvous);
        var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) }, rendezvous: rendezvous);

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromSeconds(1));

        Assert.True(wifi.MetPartner, "WiFi discovery ran without serial discovery ever starting alongside it.");
        Assert.True(serial.MetPartner, "Serial discovery ran without WiFi discovery ever starting alongside it.");
        Assert.Equal(2, result.Count);
    }

    // ------------------------------------------------------------------ result set

    [Fact]
    public async Task DiscoverAcrossTransports_ReportsWiFiBeforeSerial()
    {
        // The order callers have always seen. CreateTransportFinders builds the list in the same
        // order for the same reason.
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.Wifi) });
        var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "A", "B" }, result.Select(d => d.SerialNumber));
    }

    [Fact]
    public async Task DiscoverAcrossTransports_SameUnitOnBothTransports_StaysTwoEntries()
    {
        // Two genuine ways to connect to one device, so an agent must still see both. The identity
        // used for deduplication is transport-prefixed, which is what keeps them apart.
        var wifi = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.Wifi) });
        var serial = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.Serial) });

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromSeconds(1));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DiscoverAcrossTransports_OneTransportReportingADeviceTwice_CollapsesToOne()
    {
        var serial = new ListDeviceFinder(new[]
        {
            Info("DUP", ConnectionType.Serial, name: "first"),
            Info("DUP", ConnectionType.Serial, name: "second"),
        });

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { serial }, TimeSpan.FromSeconds(1));

        var device = Assert.Single(result);
        Assert.Equal("first", device.Name);
    }

    [Fact]
    public async Task DiscoverAcrossTransports_OneTransportFails_TheOtherStillReports()
    {
        // The reason this matters for discover_devices: a WiFi probe on a host with no usable
        // network used to throw straight out of the tool call and lose the USB device found
        // alongside it.
        var wifi = new ListDeviceFinder(
            Array.Empty<IDeviceInfo>(),
            throwOnDiscover: new InvalidOperationException("no network"));
        var serial = new ListDeviceFinder(new[] { Info("USB", ConnectionType.Serial) });

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromSeconds(1));

        var device = Assert.Single(result);
        Assert.Equal("USB", device.SerialNumber);
    }

    // ------------------------------------------------------------------ ownership

    [Fact]
    public async Task DiscoverAcrossTransports_DisposesTheFindersItWasGiven()
    {
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.Wifi) });
        var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });

        await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromSeconds(1));

        Assert.True(wifi.Disposed);
        Assert.True(serial.Disposed);
    }

    [Fact]
    public async Task DiscoverAcrossTransports_NoTransportsEnabled_ReturnsEmptyRatherThanThrowing()
    {
        // AllTransportsDeviceFinder requires at least one finder; "both transports off" is a
        // legitimate call and must not surface that as an ArgumentException.
        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            Array.Empty<IDeviceFinder>(), TimeSpan.FromSeconds(1));

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void CreateTransportFinders_BothEnabled_IsWiFiThenSerial()
    {
        var finders = DaqifiAgent.CreateTransportFinders(wifi: true, serial: true);

        Assert.Collection(
            finders,
            f => Assert.IsType<WifiDeviceFinder>(f),
            f => Assert.IsType<SerialDeviceFinder>(f));

        DisposeAll(finders);
    }

    [Theory]
    [InlineData(true, false, typeof(WifiDeviceFinder))]
    [InlineData(false, true, typeof(SerialDeviceFinder))]
    public void CreateTransportFinders_OneEnabled_BuildsOnlyThatTransport(bool wifi, bool serial, Type expected)
    {
        var finders = DaqifiAgent.CreateTransportFinders(wifi, serial);

        Assert.IsType(expected, Assert.Single(finders));

        DisposeAll(finders);
    }

    [Fact]
    public void CreateTransportFinders_NeitherEnabled_IsEmpty()
    {
        Assert.Empty(DaqifiAgent.CreateTransportFinders(wifi: false, serial: false));
    }

    [Fact]
    public async Task DiscoverAsync_NeitherTransportEnabled_ReturnsEmptyWithoutTouchingHardware()
    {
        var agent = new DaqifiAgent(new ServerOptions());

        var result = await agent.DiscoverAsync(
            timeoutMs: 1000, wifi: false, serial: false, CancellationToken.None);

        Assert.Empty(result.Devices);
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task DiscoverAcrossTransports_CallerCancel_ThrowsBeforeTimeoutElapses()
    {
        // The lie this pins: discover_devices used to give Core only the timeout and call
        // ThrowIfCancellationRequested() after WiFi had listened out the full window. A cancel at
        // ~entry must not wait that window out.
        //
        // The window is deliberately huge and the bound a small fraction of it, so the assertion
        // is "well short of the timeout" rather than "under N seconds": a loaded CI runner can
        // take seconds to schedule the continuation (a fixed 5 s bound failed at 5.08 s), but no
        // amount of jitter gets near minutes, while the regression takes the whole window. The
        // race keeps that regression from hanging the run for the full window — it fails at the
        // bound instead.
        var hanging = new ListDeviceFinder(Array.Empty<IDeviceInfo>(), listenUntilPassEnds: true);
        using var cts = new CancellationTokenSource();
        var timeout = TimeSpan.FromMinutes(10);
        var bound = timeout / 10;

        var discover = DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { hanging }, timeout, cts.Token);
        await hanging.Started;

        var started = Stopwatch.StartNew();
        cts.Cancel();

        var completed = await Task.WhenAny(discover, Task.Delay(bound));
        Assert.True(
            completed == discover,
            $"caller cancel had not returned after {started.Elapsed} of a {timeout} discovery window");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discover);
        Assert.True(hanging.Disposed);
    }

    [Fact]
    public async Task DiscoverAcrossTransports_Timeout_ReturnsEmptyRatherThanThrowing()
    {
        var hanging = new ListDeviceFinder(Array.Empty<IDeviceInfo>(), listenUntilPassEnds: true);

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { hanging }, TimeSpan.FromMilliseconds(50));

        Assert.Empty(result);
        Assert.True(hanging.Disposed);
    }

    [Fact]
    public async Task DiscoverAsync_HandsTheCallerTokenToTheFanOut()
    {
        // Pins the public entry point's wiring, not the mid-listen cancel (that is
        // DiscoverAcrossTransports_CallerCancel_ThrowsBeforeTimeoutElapses, at the seam where a
        // finder can be faked). No transport is enabled, so nothing listens: a DiscoverAsync
        // that dropped its token would answer this with an empty list instead of throwing.
        var agent = new DaqifiAgent(new ServerOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.DiscoverAsync(timeoutMs: 30_000, wifi: false, serial: false, cts.Token));
    }

    [Fact]
    public async Task DiscoverAcrossTransports_Timeout_KeepsWhatTheOtherTransportFound()
    {
        // The everyday shape of a pass: serial settles early, WiFi listens out the whole window.
        // That ordinary timeout must not become an empty result — the USB device found alongside
        // is exactly what discover_devices exists to report.
        var wifi = new ListDeviceFinder(
            new[] { Info("A", ConnectionType.Wifi) },
            listenUntilPassEnds: true);
        var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });

        var result = await DaqifiAgent.DiscoverAcrossTransportsAsync(
            new IDeviceFinder[] { wifi, serial }, TimeSpan.FromMilliseconds(100));

        Assert.Equal(new[] { "A", "B" }, result.Select(d => d.SerialNumber));
    }

    // ------------------------------------------------------------------ helpers

    private static void DisposeAll(IEnumerable<IDeviceFinder> finders)
    {
        foreach (var finder in finders)
        {
            finder.Dispose();
        }
    }

    private static IDeviceInfo Info(string serialNumber, ConnectionType connectionType, string name = "Fake") =>
        new FakeDeviceInfo
        {
            Name = name,
            SerialNumber = serialNumber,
            ConnectionType = connectionType,
            PortName = connectionType == ConnectionType.Serial ? "/dev/fake" : null,
            IPAddress = connectionType == ConnectionType.Wifi ? IPAddress.Loopback : null,
        };

    /// <summary>
    /// A meeting point for a fixed number of participants: each one announces its arrival and then
    /// waits for the rest. Used to assert that two discoveries were genuinely in flight at the same
    /// moment, which sequential execution can never satisfy.
    /// </summary>
    private sealed class Rendezvous
    {
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _participants;
        private int _arrived;

        public Rendezvous(int participants) => _participants = participants;

        /// <summary>
        /// Announces this participant and waits for the others, giving up after
        /// <see cref="RendezvousTimeoutMs"/> so a regression fails the assertion instead of
        /// hanging the whole test run.
        /// </summary>
        /// <returns>True when every participant arrived; false if the wait timed out.</returns>
        public async Task<bool> ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == _participants)
            {
                _allArrived.TrySetResult();
            }

            var completed = await Task.WhenAny(_allArrived.Task, Task.Delay(RendezvousTimeoutMs))
                .ConfigureAwait(false);
            return completed == _allArrived.Task;
        }
    }

    private sealed class ListDeviceFinder : IDeviceFinder
    {
        private readonly IReadOnlyList<IDeviceInfo> _devices;
        private readonly Exception? _throw;
        private readonly Rendezvous? _rendezvous;
        private readonly bool _listenUntilPassEnds;
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ListDeviceFinder(
            IEnumerable<IDeviceInfo> devices,
            Exception? throwOnDiscover = null,
            Rendezvous? rendezvous = null,
            bool listenUntilPassEnds = false)
        {
            _devices = devices.ToList();
            _throw = throwOnDiscover;
            _rendezvous = rendezvous;
            _listenUntilPassEnds = listenUntilPassEnds;
        }

        public bool Disposed { get; private set; }

        /// <summary>Completes once this finder has entered <see cref="DiscoverAsync"/>.</summary>
        public Task Started => _started.Task;

        /// <summary>Whether every other participant was already inside its own discovery.</summary>
        public bool MetPartner { get; private set; }

#pragma warning disable CS0067 // interface events unused by this fake
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public async Task<IReadOnlyList<IDeviceInfo>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();

            if (_rendezvous != null)
            {
                MetPartner = await _rendezvous.ArriveAndWaitAsync().ConfigureAwait(false);
            }

            if (_listenUntilPassEnds)
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pass.CancelAfter(timeout ?? Timeout.InfiniteTimeSpan);
                await Task.Delay(Timeout.InfiniteTimeSpan, pass.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_throw != null)
            {
                throw _throw;
            }

            return _devices;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDeviceInfo : IDeviceInfo
    {
        public string Name { get; set; } = "Fake";
        public string SerialNumber { get; set; } = "SN";
        public string FirmwareVersion { get; set; } = "3.7.2";
        public IPAddress? IPAddress { get; set; }
        public string? MacAddress { get; set; }
        public int? Port { get; set; }
        public IPAddress? LocalInterfaceAddress { get; set; }
        public DeviceType Type { get; set; } = DeviceType.Nyquist1;
        public bool IsPowerOn { get; set; } = true;
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
        public string? PortName { get; set; }
        public string? DevicePath { get; set; }
        public string? LocationKey { get; set; }
    }
}
