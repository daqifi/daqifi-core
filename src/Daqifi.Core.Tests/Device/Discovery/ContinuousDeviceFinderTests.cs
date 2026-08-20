using System.Diagnostics;
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

    private static DeviceInfo SerialAt(string sn, string port, string? location)
        => new()
        {
            ConnectionType = ConnectionType.Serial,
            SerialNumber = sn,
            PortName = port,
            Name = "serial",
            LocationKey = location
        };

    private static BusyPort[] Busy(string port, string? location = null)
        => new[] { new BusyPort(port, location) };

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

    #region Busy ports (issue #532)

    [Fact]
    public void Reconcile_DeviceOnABusyPort_IsNotLost()
    {
        // The reported bug: keep discovery running next to a device you are using, and the
        // device you are using disappears. Its port is still there -- your own process just has
        // it open, so the probe cannot ask it anything and answers exactly as it would for an
        // empty port.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Serial("SN-1", port: "COM3") });

        // Several passes where the probe cannot open COM3. Well past MissThreshold.
        for (var i = 0; i < 5; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        }

        Assert.Empty(lost);
        Assert.Single(finder.Devices);
    }

    [Fact]
    public void Reconcile_DeviceWhosePortIsGone_IsStillLost()
    {
        // The control, and the one that matters most: the fix must not make devices immortal.
        // A real removal takes the port out of enumeration, so no busy report mentions it.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Serial("SN-1", port: "COM3") });

        // Another port is busy; COM3 is simply absent.
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM9"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM9"));

        Assert.Single(lost);
        Assert.Equal("SN-1", lost[0].SerialNumber);
        Assert.Empty(finder.Devices);
    }

    [Fact]
    public void Reconcile_BusyPortMatching_IsCaseInsensitive()
    {
        // Windows COM names are case-insensitive, and the busy report is whatever string the
        // platform handed back. A case difference must not resurrect the original bug.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Serial("SN-1", port: "COM3") });
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("com3"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("com3"));

        Assert.Empty(lost);
    }

    [Fact]
    public void Reconcile_BusyPortRescue_DoesNotStopAGenuineLossLater()
    {
        // A device held open for a while and then genuinely unplugged must still be reported
        // lost -- the rescue suppresses the miss, it does not latch the device as present.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Serial("SN-1", port: "COM3") });
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        Assert.Empty(lost);

        // Unplugged: the port stops enumerating, so it stops being reported busy.
        finder.Reconcile(Array.Empty<IDeviceInfo>());
        finder.Reconcile(Array.Empty<IDeviceInfo>());

        Assert.Single(lost);
    }

    [Fact]
    public void Reconcile_WithNoBusyReport_BehavesExactlyAsBefore()
    {
        // A finder that cannot report busy ports (WiFi, or any third-party IDeviceFinder) passes
        // null, and nothing about the existing miss accounting changes.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { Serial("SN-1", port: "COM3") });
        finder.Reconcile(Array.Empty<IDeviceInfo>());
        finder.Reconcile(Array.Empty<IDeviceInfo>());

        Assert.Single(lost);
    }

    [Fact]
    public void Reconcile_PortNameReusedByADifferentDevice_StillReportsTheOriginalLost()
    {
        // An OS port name is a lease, not an identity. Unplug SN-1 and the next device along can
        // be handed COM3; if something holds that new port open, matching on the name alone would
        // keep SN-1 reported as present for as long as the name stayed occupied -- a device made
        // immortal, which is exactly what the rescue must not cause.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        // COM3 is busy again -- but it is a DIFFERENT physical port now.
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-4.1"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-4.1"));

        Assert.Single(lost);
        Assert.Equal("SN-1", lost[0].SerialNumber);
    }

    [Fact]
    public void Reconcile_SameLocationStillBusy_IsStillRescued()
    {
        // The complement, so the location check cannot become "never rescue": the same physical
        // port, still held by the caller's own app, must keep the device in the live set.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-1.2"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-1.2"));

        Assert.Empty(lost);
    }

    [Fact]
    public void Reconcile_WhenALocationIsUnknown_FallsBackToThePortName()
    {
        // A platform that cannot place the port degrades to name matching rather than losing the
        // rescue entirely -- never worse than before locations were carried.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: null) });
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-4.1"));
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-4.1"));

        Assert.Empty(lost);
    }

    [Fact]
    public void Reconcile_AConfirmingReportIsNotHiddenByANameOnlyOneForTheSamePort()
    {
        // The composite finder aggregates across transports, so ONE port name can now be reported
        // more than once in a pass. Returning on the first match let a report that happened to
        // carry no location downgrade the device to the bounded name-only path, even though a
        // second report confirmed the location outright. Order is deliberate: the weaker report
        // comes first, which is the only order in which the bug shows.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        var reports = new[]
        {
            new BusyPort("COM3", null),          // weaker, and seen first
            new BusyPort("COM3", "usb:1-1.2"),   // confirms the location
        };

        // Past MaxWeakBusyRescues: only a LocationConfirmed classification survives this long.
        for (var i = 0; i < 40; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), reports);
        }

        Assert.Empty(lost);
    }

    [Fact]
    public void Reconcile_AContradictedReportRescuesNothing()
    {
        // One reporter positively places this port somewhere the device is not. Combined with a
        // name-only report that is merely uninformative, the evidence is contradictory, and the
        // fail-safe reading of a contradiction is to rescue nothing rather than fall back to the
        // weakest claim available.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        var reports = new[]
        {
            new BusyPort("COM3", null),
            new BusyPort("COM3", "usb:9-9.9"),   // a different physical position
        };

        finder.Reconcile(Array.Empty<IDeviceInfo>(), reports);
        finder.Reconcile(Array.Empty<IDeviceInfo>(), reports);

        Assert.Single(lost);
    }

    [Fact]
    public void Reconcile_NameOnlyRescue_IsBounded()
    {
        // A name-only match is not evidence about the DEVICE -- an OS port name is a lease, and
        // neither side offered a location to check it against. Left unbounded, a removed device
        // whose name stayed occupied would never be reported lost: the exact failure this whole
        // mechanism must not cause. Past the bound it falls back to ordinary miss counting.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: null) });

        for (var i = 0; i < 40; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        }

        Assert.Single(lost);
        Assert.Equal("SN-1", lost[0].SerialNumber);
    }

    [Fact]
    public void Reconcile_LocationConfirmedRescue_IsNotBounded()
    {
        // The complement, and the reason a blanket cap was the wrong fix: the case this exists
        // for -- your own app holding the device it is using -- has no time limit. With the
        // location agreeing there IS evidence about the physical port, so it holds indefinitely.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        for (var i = 0; i < 200; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-1.2"));
        }

        Assert.Empty(lost);
    }

    [Fact]
    public void Reconcile_ARealSighting_ResetsTheWeakRescueBudget()
    {
        // The budget is about consecutive unverifiable passes. Actually seeing the device proves
        // it is there, so a later spell of name-only rescues starts from scratch rather than
        // inheriting an old tally and evicting a present device early.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        var device = SerialAt("SN-1", port: "COM3", location: null);
        finder.Reconcile(new[] { device });

        for (var i = 0; i < 15; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        }
        Assert.Empty(lost);

        finder.Reconcile(new[] { device });          // probed successfully again

        for (var i = 0; i < 15; i++)
        {
            finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3"));
        }

        Assert.Empty(lost);
    }

    #endregion

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

    [Fact]
    public void Reconcile_NullIdentitySelectorResult_FallsBackToDefaultIdentity()
    {
        // A selector returning null must not collapse distinct devices onto one empty key;
        // GetIdentity falls back to the default per-transport identity instead.
        using var finder = NewFinder(new StubDeviceFinder(), identitySelector: _ => null!);
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        finder.Reconcile(new IDeviceInfo[]
        {
            Wifi("AA:BB:CC:DD:EE:01"),
            Wifi("AA:BB:CC:DD:EE:02")
        });

        Assert.Equal(2, discovered.Count);
        Assert.Equal(2, finder.Devices.Count);
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

    [Fact]
    public async Task StopAsync_CancelsInFlightPass_WithoutWaitingForPassTimeout()
    {
        // PassTimeout is huge; if the in-flight pass were not cancellable, StopAsync would block
        // for ~30s. With cancellable passes it returns as soon as cancellation propagates.
        var inner = new BlockingDeviceFinder();
        using var finder = new ContinuousDeviceFinder(inner, new ContinuousDiscoveryOptions
        {
            PassTimeout = TimeSpan.FromSeconds(30),
            Interval = TimeSpan.Zero
        });

        finder.Start();
        await WaitUntil(() => inner.PassesStarted > 0);

        var sw = Stopwatch.StartNew();
        await finder.StopAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {sw.Elapsed}; expected prompt cancellation.");
        Assert.False(finder.IsRunning);
    }

    [Fact]
    public async Task Dispose_WithInFlightPass_StopsPromptlyAndDisposesInnerFinder()
    {
        var inner = new BlockingDeviceFinder();
        var finder = new ContinuousDeviceFinder(inner, new ContinuousDiscoveryOptions
        {
            PassTimeout = TimeSpan.FromSeconds(30),
            Interval = TimeSpan.Zero
        });

        finder.Start();
        await WaitUntil(() => inner.PassesStarted > 0);

        var sw = Stopwatch.StartNew();
        finder.Dispose(); // must cancel the in-flight pass, not wait out the 30s PassTimeout
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.Elapsed}; expected prompt cancellation.");
        Assert.True(inner.Disposed); // inner finder disposed only after the loop stopped
    }

    private static async Task<T> AwaitSignal<T>(TaskCompletionSource<T> signal)
    {
        var completed = await Task.WhenAny(signal.Task, Task.Delay(WaitTimeout));
        Assert.True(completed == signal.Task, "Timed out waiting for the expected event.");
        return await signal.Task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < WaitTimeout, "Timed out waiting for the expected condition.");
            await Task.Delay(10);
        }
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

    /// <summary>
    /// Finder whose pass blocks until its cancellation token fires, then returns an empty set.
    /// Used to verify that Stop/Dispose cancel an in-flight pass instead of waiting out a large
    /// <see cref="ContinuousDiscoveryOptions.PassTimeout"/>.
    /// </summary>
    private sealed class BlockingDeviceFinder : IDeviceFinder, IDisposable
    {
        private int _passesStarted;

        public int PassesStarted => Volatile.Read(ref _passesStarted);
        public bool Disposed { get; private set; }

#pragma warning disable CS0067 // Events are part of the interface but unused by this fake.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _passesStarted);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                await cancelled.Task.ConfigureAwait(false);
            }

            return Array.Empty<IDeviceInfo>();
        }

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
            => DiscoverAsync(CancellationToken.None);

        public void Dispose() => Disposed = true;
    }

    #endregion
}
