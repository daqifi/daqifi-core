using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Xunit;

namespace Daqifi.Core.Tests.Device.Discovery
{
    public class AllTransportsDeviceFinderTests
    {
        [Fact]
        public async Task DiscoverAsync_FansOutAcrossFinders_ReturnsUnion()
        {
            var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) });
            var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { wifi, serial });

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Equal(2, result.Count);
            Assert.Contains(result, d => d.SerialNumber == "A");
            Assert.Contains(result, d => d.SerialNumber == "B");
        }

        [Fact]
        public async Task DiscoverAsync_DuplicateFromTwoFindersSameTransport_DedupedFirstWins()
        {
            // Same serial + same transport => same default identity => one entry, first finder wins.
            var first = new ListDeviceFinder(new[] { Info("DUP", ConnectionType.Serial, name: "first") });
            var second = new ListDeviceFinder(new[] { Info("DUP", ConnectionType.Serial, name: "second") });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { first, second });

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Single(result);
            Assert.Equal("first", result[0].Name);
        }

        [Fact]
        public async Task DiscoverAsync_SameDeviceOnTwoTransports_KeptAsTwoEntries()
        {
            // Default identity is per-transport (issue #245): a device reachable on both USB and
            // WiFi is two genuine ways to connect, so it is intentionally NOT collapsed.
            var wifi = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.WiFi) });
            var serial = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { wifi, serial });

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task DiscoverAsync_CustomIdentitySelector_CollapsesAcrossTransports()
        {
            var wifi = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.WiFi) });
            var serial = new ListDeviceFinder(new[] { Info("SHARED", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { wifi, serial },
                identitySelector: d => "sn:" + d.SerialNumber);

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Single(result); // both share serial "SHARED" under the custom selector
        }

        [Fact]
        public async Task DiscoverAsync_EmptyIdentitySelectorResult_FallsBackToDefault()
        {
            // Selector returns empty => must fall back to the per-transport default (which keeps
            // these two distinct), rather than collapsing everything onto one empty key.
            var a = new ListDeviceFinder(new[] { Info("A", ConnectionType.Serial) });
            var b = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { a, b },
                identitySelector: _ => "   ");

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task DiscoverAsync_OneFinderThrows_OthersStillReturned()
        {
            var good = new ListDeviceFinder(new[] { Info("OK", ConnectionType.Serial) });
            var bad = new ListDeviceFinder(Array.Empty<IDeviceInfo>(), throwOnDiscover: new InvalidOperationException("wifi down"));
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { bad, good });

            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Single(result);
            Assert.Equal("OK", result[0].SerialNumber);
        }

        [Fact]
        public async Task DiscoverAsync_RaisesDeviceDiscoveredPerUnique_AndCompletedOnce()
        {
            var wifi = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) });
            var serial = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { wifi, serial });

            var discovered = new List<string>();
            var completed = 0;
            finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo.SerialNumber);
            finder.DiscoveryCompleted += (_, _) => completed++;

            await finder.DiscoverAsync();

            Assert.Equal(new[] { "A", "B" }, discovered.OrderBy(s => s).ToArray());
            Assert.Equal(1, completed);
        }

        [Fact]
        public async Task DiscoverAsync_Cancelled_PropagatesCancellation()
        {
            var finder = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new ListDeviceFinder(Array.Empty<IDeviceInfo>(), observeCancellation: true) });
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finder.DiscoverAsync(cts.Token));
        }

        [Fact]
        public async Task DiscoverAsync_TimeoutOverload_ReturnsResults()
        {
            var finder = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new ListDeviceFinder(new[] { Info("A", ConnectionType.Serial) }) });

            var result = (await finder.DiscoverAsync(TimeSpan.FromSeconds(1))).ToList();

            Assert.Single(result);
        }

        [Fact]
        public void Dispose_CallerSuppliedFinders_AreNotDisposed()
        {
            var supplied = new ListDeviceFinder(Array.Empty<IDeviceInfo>());
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { supplied });

            finder.Dispose();

            Assert.False(supplied.Disposed); // caller owns supplied finders
        }

        [Fact]
        public async Task DiscoverAsync_AfterDispose_Throws()
        {
            var finder = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new ListDeviceFinder(Array.Empty<IDeviceInfo>()) });
            finder.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
        }

        [Fact]
        public void Constructor_NullFinders_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AllTransportsDeviceFinder(null!));
        }

        [Fact]
        public void Constructor_EmptyFinders_Throws()
        {
            Assert.Throws<ArgumentException>(() => new AllTransportsDeviceFinder(Array.Empty<IDeviceFinder>()));
        }

        [Fact]
        public void Constructor_ContainsNull_Throws()
        {
            Assert.Throws<ArgumentException>(() => new AllTransportsDeviceFinder(new IDeviceFinder[] { null! }));
        }

        [Fact]
        public void CreateDefault_ReturnsUsableFinder()
        {
            using var finder = AllTransportsDeviceFinder.CreateDefault();
            Assert.NotNull(finder);
        }

        [Fact]
        public async Task DiscoverAsync_ThrowingDeviceDiscoveredSubscriber_StillReturnsAllAndCompletes()
        {
            var a = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) });
            var b = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { a, b });

            finder.DeviceDiscovered += (_, _) => throw new InvalidOperationException("boom");
            var completed = 0;
            finder.DiscoveryCompleted += (_, _) => completed++;

            // A throwing subscriber must not abort the dedup loop or skip DiscoveryCompleted.
            var result = (await finder.DiscoverAsync()).ToList();

            Assert.Equal(2, result.Count);
            Assert.Equal(1, completed);
        }

        [Fact]
        public async Task DiscoverAsync_Timeout_FinderThrowsCancellation_ReturnsOthersAndCompletes()
        {
            // A finder that throws OperationCanceledException on its timeout overload must be treated
            // as a normal (empty) end-of-pass, not propagate out and skip DiscoveryCompleted.
            var throwsOce = new ListDeviceFinder(new[] { Info("A", ConnectionType.WiFi) }, throwOnDiscover: new OperationCanceledException());
            var ok = new ListDeviceFinder(new[] { Info("B", ConnectionType.Serial) });
            var finder = new AllTransportsDeviceFinder(new IDeviceFinder[] { throwsOce, ok });

            var completed = 0;
            finder.DiscoveryCompleted += (_, _) => completed++;

            var result = (await finder.DiscoverAsync(TimeSpan.FromMilliseconds(50))).ToList();

            Assert.Single(result);
            Assert.Equal("B", result[0].SerialNumber);
            Assert.Equal(1, completed);
        }

        // ---- DaqifiDeviceFactory.DiscoverAndConnectAsync selection paths ----

        [Fact]
        public async Task DiscoverAndConnectAsync_NoDeviceFound_Throws()
        {
            var empty = new ListDeviceFinder(Array.Empty<IDeviceInfo>());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DaqifiDeviceFactory.DiscoverAndConnectAsync(finder: empty));
        }

        [Fact]
        public async Task DiscoverAndConnectAsync_FilterMatchesNothing_Throws()
        {
            var finder = new ListDeviceFinder(new[] { Info("A", ConnectionType.Serial) });
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DaqifiDeviceFactory.DiscoverAndConnectAsync(
                    filter: d => d.SerialNumber == "does-not-exist",
                    finder: finder));
        }

        [Fact]
        public async Task DiscoverAndConnectAsync_AlreadyCancelled_Throws()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => DaqifiDeviceFactory.DiscoverAndConnectAsync(
                    finder: new ListDeviceFinder(Array.Empty<IDeviceInfo>()),
                    cancellationToken: cts.Token));
        }

        #region Helpers

        private static FakeDeviceInfo Info(string serial, ConnectionType type, string? name = null) => new()
        {
            SerialNumber = serial,
            ConnectionType = type,
            Name = name ?? serial,
        };

        #region Busy-port forwarding (#532)

        [Fact]
        public void BusyPorts_AreForwardedFromTheConstituentThatReportsThem()
        {
            // ContinuousDeviceFinder asks the finder it DIRECTLY wraps for busy ports:
            //   (_finder as IBusyPortReporter)?.BusyPortsFromLastPass
            // and the documented way to build continuous all-transport discovery is to wrap this
            // composite. So if the composite does not carry the signal, the cast yields null, the
            // rescue never runs, and a device whose port the app itself holds is reported lost --
            // #532, unfixed, on the one path most callers actually use.
            //
            // The `as` (rather than a direct cast) is deliberate: it is what the consumer does,
            // and it compiles whether or not the composite implements the interface, so this test
            // fails by ASSERTION on the unfixed code rather than by not building.
            var reporting = new BusyReportingFinder("COM3", "usb:1-1.2");
            IDeviceFinder composite = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new ListDeviceFinder(Enumerable.Empty<IDeviceInfo>()), reporting });

            var reporter = composite as IBusyPortReporter;

            Assert.NotNull(reporter);
            var ports = reporter!.BusyPortsFromLastPass;
            Assert.Equal(new[] { "COM3" }, ports.Select(p => p.PortName));
            Assert.Equal("usb:1-1.2", ports.Single().LocationKey);
        }

        [Fact]
        public void BusyPorts_AggregateAcrossConstituentsAndIgnoreNonReporters()
        {
            // A composite mixes transports; only some report busy ports at all. Those that do not
            // must be skipped rather than suppressing the ones that do.
            IDeviceFinder composite = new AllTransportsDeviceFinder(new IDeviceFinder[]
            {
                new BusyReportingFinder("COM3", null),
                new ListDeviceFinder(Enumerable.Empty<IDeviceInfo>()),                 // reports nothing -- not a reporter
                new BusyReportingFinder("COM7", "usb:2-1"),
            });

            var ports = ((IBusyPortReporter)composite).BusyPortsFromLastPass;

            Assert.Equal(new[] { "COM3", "COM7" }, ports.Select(p => p.PortName).OrderBy(n => n));
        }

        [Fact]
        public void BusyPorts_AggregateThroughANestedComposite()
        {
            // Nesting needs no special handling precisely BECAUSE the composite implements the
            // interface -- the same filter picks up an inner composite and recurses. Pinned so a
            // later "simplification" to a concrete SerialDeviceFinder check would fail here.
            IDeviceFinder inner = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new BusyReportingFinder("COM9", null) });
            IDeviceFinder outer = new AllTransportsDeviceFinder(
                new IDeviceFinder[] { new ListDeviceFinder(Enumerable.Empty<IDeviceInfo>()), inner });

            var ports = ((IBusyPortReporter)outer).BusyPortsFromLastPass;

            Assert.Equal(new[] { "COM9" }, ports.Select(p => p.PortName));
        }

        /// <summary>Finds nothing, but reports one port as present-and-already-open.</summary>
        private sealed class BusyReportingFinder : IDeviceFinder, IBusyPortReporter
        {
            private readonly BusyPort _busy;

            public BusyReportingFinder(string portName, string? locationKey)
                => _busy = new BusyPort(portName, locationKey);

#pragma warning disable CS0067 // Interface events, unused by this stub.
            public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
            public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

            public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

            public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
                => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

            IReadOnlyCollection<BusyPort> IBusyPortReporter.BusyPortsFromLastPass => new[] { _busy };
        }

        #endregion

        private sealed class ListDeviceFinder : IDeviceFinder, IDisposable
        {
            private readonly IReadOnlyList<IDeviceInfo> _devices;
            private readonly Exception? _throw;
            private readonly bool _observeCancellation;

            public ListDeviceFinder(IEnumerable<IDeviceInfo> devices, Exception? throwOnDiscover = null, bool observeCancellation = false)
            {
                _devices = devices.ToList();
                _throw = throwOnDiscover;
                _observeCancellation = observeCancellation;
            }

            public bool Disposed { get; private set; }

#pragma warning disable CS0067 // interface events unused by this fake
            public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
            public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

            public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
            {
                if (_observeCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_throw != null)
                {
                    throw _throw;
                }

                return Task.FromResult<IEnumerable<IDeviceInfo>>(_devices);
            }

            public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout) => DiscoverAsync(CancellationToken.None);

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
            public Daqifi.Core.Device.Discovery.DeviceType Type { get; set; } = Daqifi.Core.Device.Discovery.DeviceType.Nyquist1;
            public bool IsPowerOn { get; set; } = true;
            public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
            public string? PortName { get; set; }
            public string? DevicePath { get; set; }
        }

        #endregion
    }
}
