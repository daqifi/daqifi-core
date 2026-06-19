using System.Net;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class ContinuousDeviceFinderTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static DeviceInfo Wifi(string mac, string sn = "SN", string name = "wifi", IPAddress? ip = null, int port = 9760)
        => new()
        {
            ConnectionType = ConnectionType.WiFi,
            MacAddress = mac,
            SerialNumber = sn,
            Name = name,
            IPAddress = ip,
            Port = port
        };

    private static DeviceInfo Serial(string sn, string port = "COM3", string name = "serial")
        => new()
        {
            ConnectionType = ConnectionType.Serial,
            SerialNumber = sn,
            PortName = port,
            Name = name
        };

    private static ContinuousDeviceFinder NewFinder(
        IDeviceFinder inner,
        int missThreshold = 2,
        Func<IDeviceInfo, string>? identitySelector = null,
        bool leaveInnerFinderOpen = false)
        => new(inner, new ContinuousDiscoveryOptions
        {
            MissThreshold = missThreshold,
            IdentitySelector = identitySelector,
            LeaveInnerFinderOpen = leaveInnerFinderOpen,
            // Tiny cadence keeps the loop tests responsive without relying on wall-clock sleeps.
            Interval = TimeSpan.FromMilliseconds(10),
            PassTimeout = TimeSpan.FromMilliseconds(50)
        });

    #region Constructor / options validation

    [Fact]
    public void Constructor_NullFinder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ContinuousDeviceFinder(null!));
    }

    [Fact]
    public void Constructor_NullOptions_UsesDefaults()
    {
        using var finder = new ContinuousDeviceFinder(new StubDeviceFinder());
        Assert.False(finder.IsRunning);
        Assert.Empty(finder.Devices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_MissThresholdBelowOne_Throws(int missThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContinuousDeviceFinder(
            new StubDeviceFinder(), new ContinuousDiscoveryOptions { MissThreshold = missThreshold }));
    }

    [Fact]
    public void Constructor_NonPositivePassTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContinuousDeviceFinder(
            new StubDeviceFinder(), new ContinuousDiscoveryOptions { PassTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContinuousDeviceFinder(
            new StubDeviceFinder(), new ContinuousDiscoveryOptions { Interval = TimeSpan.FromSeconds(-1) }));
    }

    #endregion

    #region Reconcile: dedup + discovery

    [Fact]
    public void Reconcile_NewDevice_RaisesDiscoveredAndAddsToLiveSet()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        var device = Wifi("AA:BB:CC:DD:EE:01");
        finder.Reconcile(new[] { device });

        Assert.Single(discovered);
        Assert.Same(device, discovered[0]);
        Assert.Single(finder.Devices);
        Assert.Same(device, finder.Devices[0]);
    }

    [Fact]
    public void Reconcile_SameDeviceAcrossPasses_RaisesDiscoveredOnce()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });
        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });
        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });

        Assert.Single(discovered);
        Assert.Single(finder.Devices);
    }

    [Fact]
    public void Reconcile_DuplicateDevicesWithinSinglePass_RaisesDiscoveredOnce()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new[]
        {
            Wifi("AA:BB:CC:DD:EE:01"),
            Wifi("AA:BB:CC:DD:EE:01")
        });

        Assert.Single(discovered);
        Assert.Single(finder.Devices);
    }

    [Fact]
    public void Reconcile_RefreshesMetadataForExistingDevice_WithoutRediscovering()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.10")) });
        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.20")) });

        Assert.Single(discovered);
        Assert.Single(finder.Devices);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), finder.Devices[0].IPAddress);
    }

    #endregion

    #region Reconcile: stale removal

    [Fact]
    public void Reconcile_DeviceAbsent_NotLostUntilMissThresholdReached()
    {
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });
        finder.Reconcile(Array.Empty<IDeviceInfo>()); // first miss — still tolerated

        Assert.Empty(lost);
        Assert.Single(finder.Devices);
    }

    [Fact]
    public void Reconcile_DeviceAbsentForThresholdPasses_RaisesLostAndRemoves()
    {
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        var device = Wifi("AA:BB:CC:DD:EE:01");
        finder.Reconcile(new[] { device });
        finder.Reconcile(Array.Empty<IDeviceInfo>()); // miss 1
        finder.Reconcile(Array.Empty<IDeviceInfo>()); // miss 2 -> lost

        Assert.Single(lost);
        Assert.Same(device, lost[0]);
        Assert.Empty(finder.Devices);
    }

    [Fact]
    public void Reconcile_MissThresholdOne_LostAfterSingleAbsence()
    {
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 1);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });
        finder.Reconcile(Array.Empty<IDeviceInfo>());

        Assert.Single(lost);
        Assert.Empty(finder.Devices);
    }

    [Fact]
    public void Reconcile_DeviceReappearsBeforeThreshold_ResetsMissCounter()
    {
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        var discovered = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") });
        finder.Reconcile(Array.Empty<IDeviceInfo>());          // miss 1
        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") }); // reappears -> reset
        finder.Reconcile(Array.Empty<IDeviceInfo>());          // miss 1 again, not lost

        Assert.Empty(lost);
        Assert.Single(discovered); // reappearance did not re-raise discovered
        Assert.Single(finder.Devices);
    }

    [Fact]
    public void Reconcile_DeviceLostThenReappears_RaisesDiscoveredAgain()
    {
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 1);
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") }); // discovered
        finder.Reconcile(Array.Empty<IDeviceInfo>());          // lost
        finder.Reconcile(new[] { Wifi("AA:BB:CC:DD:EE:01") }); // rediscovered

        Assert.Equal(2, discovered.Count);
    }

    #endregion

    #region Reconcile: identity

    [Fact]
    public void DefaultIdentity_SameSerialDifferentTransport_AreDistinct()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        // Same serial number, but one on WiFi (MAC-keyed) and one on Serial (port-keyed):
        // these are two distinct connection options the UI should show separately.
        finder.Reconcile(new IDeviceInfo[]
        {
            Wifi("AA:BB:CC:DD:EE:01", sn: "SHARED"),
            Serial("SHARED")
        });

        Assert.Equal(2, discovered.Count);
        Assert.Equal(2, finder.Devices.Count);
    }

    [Fact]
    public void DefaultIdentity_FallsBackThroughDiscriminators()
    {
        // MAC wins over everything; serial-only device keys on serial; name is the last resort.
        var wifi = Wifi("AA:BB:CC:DD:EE:01", sn: "SN1");
        Assert.StartsWith("WiFi|mac:", ContinuousDeviceFinder.DefaultIdentity(wifi));

        var hid = new DeviceInfo { ConnectionType = ConnectionType.Hid, DevicePath = "path-1", SerialNumber = "SN2" };
        Assert.Equal("Hid|path:path-1", ContinuousDeviceFinder.DefaultIdentity(hid));

        var serial = Serial("SN3", port: "COM7");
        Assert.Equal("Serial|sn:SN3", ContinuousDeviceFinder.DefaultIdentity(serial));

        var nameOnly = new DeviceInfo { ConnectionType = ConnectionType.Unknown, Name = "Bare" };
        Assert.Equal("Unknown|name:Bare", ContinuousDeviceFinder.DefaultIdentity(nameOnly));
    }

    [Fact]
    public void Reconcile_ThrowingDiscoveredHandler_DoesNotSuppressOtherDevicesOrThrow()
    {
        // A faulty subscriber must neither kill reconciliation nor suppress notifications
        // for the other devices in the same pass; the fault is surfaced via ScanError.
        using var finder = NewFinder(new StubDeviceFinder());
        var errors = new List<Exception>();
        var discovered = new List<IDeviceInfo>();
        finder.ScanError += (_, e) => errors.Add(e.Exception);
        finder.DeviceDiscovered += (_, e) =>
        {
            if (e.DeviceInfo.SerialNumber == "BAD")
            {
                throw new InvalidOperationException("handler boom");
            }

            discovered.Add(e.DeviceInfo);
        };

        // "BAD" is processed first, so without per-callback guarding its throw would
        // suppress "GOOD".
        finder.Reconcile(new IDeviceInfo[]
        {
            Serial("BAD", port: "COM1"),
            Serial("GOOD", port: "COM2")
        });

        Assert.Contains(discovered, d => d.SerialNumber == "GOOD");
        Assert.Single(errors);
        Assert.Equal(2, finder.Devices.Count); // both tracked regardless of the handler fault
    }

    [Fact]
    public void Reconcile_CustomIdentitySelector_GroupsByCustomKey()
    {
        // Collapse everything to a single key so two physically-distinct devices are
        // treated as one — proves the selector is honored.
        using var finder = NewFinder(new StubDeviceFinder(), identitySelector: _ => "constant");
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new IDeviceInfo[]
        {
            Wifi("AA:BB:CC:DD:EE:01"),
            Wifi("AA:BB:CC:DD:EE:02")
        });

        Assert.Single(discovered);
        Assert.Single(finder.Devices);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public async Task Start_WhenAlreadyRunning_Throws()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        finder.Start();
        try
        {
            Assert.Throws<InvalidOperationException>(() => finder.Start());
        }
        finally
        {
            await finder.StopAsync();
        }
    }

    [Fact]
    public void Start_AfterDispose_ThrowsObjectDisposedException()
    {
        var finder = NewFinder(new StubDeviceFinder());
        finder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => finder.Start());
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_DoesNotThrow()
    {
        using var finder = NewFinder(new StubDeviceFinder());
        await finder.StopAsync();
        Assert.False(finder.IsRunning);
    }

    [Fact]
    public void Dispose_DisposesInnerFinder_WhenNotLeaveOpen()
    {
        var inner = new StubDeviceFinder();
        var finder = NewFinder(inner, leaveInnerFinderOpen: false);

        finder.Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Dispose_LeavesInnerFinderOpen_WhenConfigured()
    {
        var inner = new StubDeviceFinder();
        var finder = NewFinder(inner, leaveInnerFinderOpen: true);

        finder.Dispose();

        Assert.False(inner.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var finder = NewFinder(new StubDeviceFinder());
        finder.Dispose();
        finder.Dispose(); // should not throw
    }

    #endregion

    #region Scan loop integration

    [Fact]
    public async Task Start_DiscoversDevice_RaisesDeviceDiscoveredAndPopulatesDevices()
    {
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var inner = new ScriptedDeviceFinder(new[] { (IReadOnlyList<IDeviceInfo>)new[] { device } });
        using var finder = NewFinder(inner);

        var discoveredSignal = new TaskCompletionSource<IDeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.DeviceDiscovered += (_, e) => discoveredSignal.TrySetResult(e.DeviceInfo);

        finder.Start();
        var discovered = await AwaitSignal(discoveredSignal);
        await finder.StopAsync();

        Assert.Same(device, discovered);
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, device));
    }

    [Fact]
    public async Task Start_DeviceDisappears_RaisesDeviceLost()
    {
        var device = Wifi("AA:BB:CC:DD:EE:01");
        // One pass sees the device; every later pass returns nothing.
        var inner = new ScriptedDeviceFinder(new[] { (IReadOnlyList<IDeviceInfo>)new[] { device } });
        using var finder = NewFinder(inner, missThreshold: 1);

        var lostSignal = new TaskCompletionSource<IDeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.DeviceLost += (_, e) => lostSignal.TrySetResult(e.DeviceInfo);

        finder.Start();
        var lost = await AwaitSignal(lostSignal);
        await finder.StopAsync();

        Assert.Same(device, lost);
        Assert.DoesNotContain(finder.Devices, d => ReferenceEquals(d, device));
    }

    [Fact]
    public async Task Start_PassThrows_RaisesScanErrorAndKeepsRunning()
    {
        var inner = new ScriptedDeviceFinder(Array.Empty<IReadOnlyList<IDeviceInfo>>())
        {
            ThrowOnEveryPass = new InvalidOperationException("boom")
        };
        using var finder = NewFinder(inner);

        var errorSignal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.ScanError += (_, e) => errorSignal.TrySetResult(e.Exception);

        finder.Start();
        var error = await AwaitSignal(errorSignal);

        Assert.IsType<InvalidOperationException>(error);
        Assert.True(finder.IsRunning); // a thrown pass must not kill the loop

        await finder.StopAsync();
    }

    [Fact]
    public async Task Start_ThrowingIdentitySelector_RaisesScanErrorAndKeepsRunning()
    {
        var inner = new ScriptedDeviceFinder(new[] { (IReadOnlyList<IDeviceInfo>)new[] { Wifi("AA:BB:CC:DD:EE:01") } });
        using var finder = NewFinder(inner, identitySelector: _ => throw new InvalidOperationException("selector boom"));

        var errorSignal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.ScanError += (_, e) => errorSignal.TrySetResult(e.Exception);

        finder.Start();
        var error = await AwaitSignal(errorSignal);

        Assert.IsType<InvalidOperationException>(error);
        Assert.True(finder.IsRunning); // a throwing identity selector must not kill the loop

        await finder.StopAsync();
    }

    private static async Task<T> AwaitSignal<T>(TaskCompletionSource<T> signal)
    {
        var completed = await Task.WhenAny(signal.Task, Task.Delay(WaitTimeout));
        Assert.True(completed == signal.Task, "Timed out waiting for the expected event.");
        return await signal.Task;
    }

    #endregion

    #region DeviceLostEventArgs

    [Fact]
    public void DeviceLostEventArgs_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceLostEventArgs(null!));
    }

    #endregion

    #region Fakes

    /// <summary>
    /// Minimal finder used by tests that drive <see cref="ContinuousDeviceFinder.Reconcile"/>
    /// directly or that only need lifecycle behavior. Each pass returns no devices.
    /// </summary>
    private sealed class StubDeviceFinder : IDeviceFinder, IDisposable
    {
        public bool Disposed { get; private set; }

#pragma warning disable CS0067 // Events are part of the interface but unused by this stub.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
            => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Finder that returns a scripted sequence of pass results. After the script is
    /// exhausted, every subsequent pass returns an empty set (so devices disappear),
    /// which is what the stale-removal loop tests rely on.
    /// </summary>
    private sealed class ScriptedDeviceFinder : IDeviceFinder
    {
        private readonly Queue<IReadOnlyList<IDeviceInfo>> _passes;

        public ScriptedDeviceFinder(IReadOnlyList<IReadOnlyList<IDeviceInfo>> passes)
        {
            _passes = new Queue<IReadOnlyList<IDeviceInfo>>(passes);
        }

        public Exception? ThrowOnEveryPass { get; init; }

#pragma warning disable CS0067 // Events are part of the interface but unused by this fake.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
            => DiscoverAsync(TimeSpan.Zero);

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
        {
            if (ThrowOnEveryPass != null)
            {
                throw ThrowOnEveryPass;
            }

            IReadOnlyList<IDeviceInfo> next;
            lock (_passes)
            {
                next = _passes.Count > 0 ? _passes.Dequeue() : Array.Empty<IDeviceInfo>();
            }

            return Task.FromResult<IEnumerable<IDeviceInfo>>(next);
        }
    }

    #endregion
}
