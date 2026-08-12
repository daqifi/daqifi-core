using System.Net;
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
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) }, rendezvous: rendezvous);
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
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) });
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
        var wifi = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.WiFi) });
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
        var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) });
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
            f => Assert.IsType<WiFiDeviceFinder>(f),
            f => Assert.IsType<SerialDeviceFinder>(f));

        DisposeAll(finders);
    }

    [Theory]
    [InlineData(true, false, typeof(WiFiDeviceFinder))]
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

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------ helpers

    private static void DisposeAll(IEnumerable<IDeviceFinder> finders)
    {
        foreach (var finder in finders)
        {
            (finder as IDisposable)?.Dispose();
        }
    }

    private static IDeviceInfo Info(string serialNumber, ConnectionType connectionType, string name = "Fake") =>
        new FakeDeviceInfo
        {
            Name = name,
            SerialNumber = serialNumber,
            ConnectionType = connectionType,
            PortName = connectionType == ConnectionType.Serial ? "/dev/fake" : null,
            IPAddress = connectionType == ConnectionType.WiFi ? IPAddress.Loopback : null,
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

    private sealed class ListDeviceFinder : IDeviceFinder, IDisposable
    {
        private readonly IReadOnlyList<IDeviceInfo> _devices;
        private readonly Exception? _throw;
        private readonly Rendezvous? _rendezvous;

        public ListDeviceFinder(
            IEnumerable<IDeviceInfo> devices,
            Exception? throwOnDiscover = null,
            Rendezvous? rendezvous = null)
        {
            _devices = devices.ToList();
            _throw = throwOnDiscover;
            _rendezvous = rendezvous;
        }

        public bool Disposed { get; private set; }

        /// <summary>Whether every other participant was already inside its own discovery.</summary>
        public bool MetPartner { get; private set; }

#pragma warning disable CS0067 // interface events unused by this fake
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            if (_rendezvous != null)
            {
                MetPartner = await _rendezvous.ArriveAndWaitAsync().ConfigureAwait(false);
            }

            if (_throw != null)
            {
                throw _throw;
            }

            return _devices;
        }

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout) =>
            DiscoverAsync(CancellationToken.None);

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
    }
}
