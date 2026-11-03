using System.Diagnostics;
using System.Net;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Tests.TestSupport;

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
    public void Reconcile_ARescueClearsTheConsecutiveMissCount()
    {
        // MissThreshold counts CONSECUTIVE misses, and the default of 2 exists to tolerate one
        // dropped response. A rescue is explicitly not a miss, so a stale count surviving one
        // silently voided that tolerance: miss, rescue, miss fired DeviceLost after a single real
        // miss FOLLOWING a pass in which the device was affirmatively confirmed present.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        finder.Reconcile(Array.Empty<IDeviceInfo>());                          // miss 1
        finder.Reconcile(Array.Empty<IDeviceInfo>(), Busy("COM3", "usb:1-1.2")); // rescued
        finder.Reconcile(Array.Empty<IDeviceInfo>());                          // miss 1 again

        Assert.Empty(lost);

        // And the tolerance is restored rather than merely deferred: it still takes a FULL
        // threshold of consecutive misses from here.
        finder.Reconcile(Array.Empty<IDeviceInfo>());
        Assert.Single(lost);
    }

    [Fact]
    public void Reconcile_AContradictedConfirmationRescuesNothing()
    {
        // A confirming report does not get to short-circuit the scan. One reporter places this
        // port where the device is; another places it somewhere else. Because the confirmed path
        // is UNBOUNDED, accepting it on contradictory evidence keeps an absent device alive
        // forever, while declining costs a miss the threshold absorbs. Order matters: the
        // confirming report comes first, which is the order an early return would have accepted.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { SerialAt("SN-1", port: "COM3", location: "usb:1-1.2") });

        var reports = new[]
        {
            new BusyPort("COM3", "usb:1-1.2"),   // confirms, and is seen FIRST
            new BusyPort("COM3", "usb:9-9.9"),   // contradicts it
        };

        finder.Reconcile(Array.Empty<IDeviceInfo>(), reports);
        finder.Reconcile(Array.Empty<IDeviceInfo>(), reports);

        Assert.Single(lost);
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
        // A metadata-only change raises NO event of any kind: not Discovered (the device is
        // already known), not Lost (it is still there), not ScanError. The live set quietly
        // carries the newer instance. This is half of what issue #725 is about — the other half
        // is the timed-out pass below — so the assertions name every channel, not just the one
        // the original version of this test checked.
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        var first = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.10"));
        var renumbered = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.20"));

        finder.Reconcile(new[] { first });
        finder.Reconcile(new[] { renumbered });

        Assert.Single(discovered);
        Assert.Empty(lost);
        Assert.Empty(errors);
        Assert.Single(finder.Devices);
        Assert.Same(renumbered, finder.Devices[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), finder.Devices[0].IPAddress);
    }

    [Fact]
    public void Reconcile_APassThatRepeatsTheSameDeviceUnchanged_RaisesNothingAtAll()
    {
        // The quiet pass. A device that is simply still there produces no event, which is
        // correct and is also why a consumer cannot tell this pass from one that never ran.
        var device = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.10"));
        using var finder = NewFinder(new StubDeviceFinder());
        var discovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Reconcile(new[] { device });
        discovered.Clear();

        finder.Reconcile(new[] { device });
        finder.Reconcile(new[] { device });

        Assert.Empty(discovered);
        Assert.Empty(lost);
        Assert.Empty(errors);
        Assert.Single(finder.Devices);
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

    [Fact]
    public void Reconcile_PartialResults_KeepsTheDevicesThePassDidNotSee()
    {
        // A pass that runs out of time can answer with only the part of the system it reached.
        // SerialDeviceFinder does exactly that: it catches the pass cancellation and returns the
        // port probes that had already finished. Absence from that list is not evidence that a
        // device went away, so it must not count as a miss. With MissThreshold 1 the old
        // behaviour removed a device that was still plugged in on the very first partial pass.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 1);
        var lost = new List<IDeviceInfo>();
        var discovered = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);

        var reached = Serial("SN-1", port: "COM3");
        var notReached = Serial("SN-2", port: "COM7");
        finder.Reconcile(new[] { (IDeviceInfo)reached, notReached });

        // The partial pass reached COM3 and a device on COM9 that is new, and never got to COM7.
        var arrived = Serial("SN-3", port: "COM9");
        var changes = finder.Reconcile(
            new[] { (IDeviceInfo)reached, arrived },
            busyPorts: null,
            resultsMayBePartial: true);

        Assert.Empty(lost);
        Assert.Equal(0, changes.LostDevices);
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, notReached));

        // What the pass did find is still taken at face value: seeing a device is positive
        // evidence whether or not the pass finished.
        Assert.Equal(2, changes.DevicesSeen);
        Assert.Equal(1, changes.NewDevices);
        Assert.Contains(discovered, d => ReferenceEquals(d, arrived));
        Assert.Equal(3, finder.Devices.Count);
    }

    [Fact]
    public void Reconcile_PartialResultsThenACompletePass_StillLosesADeviceThatIsGone()
    {
        // The partial pass holds a device; it does not make it immortal. The next pass that
        // actually finishes decides, and a device absent from THAT list is lost as before.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 1);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        var device = Serial("SN-1", port: "COM3");
        finder.Reconcile(new[] { (IDeviceInfo)device });
        finder.Reconcile(Array.Empty<IDeviceInfo>(), busyPorts: null, resultsMayBePartial: true);

        Assert.Empty(lost);

        var changes = finder.Reconcile(Array.Empty<IDeviceInfo>());

        Assert.Single(lost);
        Assert.Same(device, lost[0]);
        Assert.Equal(1, changes.LostDevices);
        Assert.Empty(finder.Devices);
    }

    [Fact]
    public void Reconcile_PartialResults_DoesNotResetTheMissCountItSkipped()
    {
        // A partial pass neither counts a miss nor forgives the misses already counted. With
        // MissThreshold 2, one real miss followed by a partial pass followed by one real miss is
        // still two counted misses, so the device is lost on the second one.
        using var finder = NewFinder(new StubDeviceFinder(), missThreshold: 2);
        var lost = new List<IDeviceInfo>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);

        finder.Reconcile(new[] { (IDeviceInfo)Serial("SN-1", port: "COM3") });
        finder.Reconcile(Array.Empty<IDeviceInfo>());                                     // miss 1
        finder.Reconcile(Array.Empty<IDeviceInfo>(), null, resultsMayBePartial: true);    // not a miss
        Assert.Empty(lost);

        finder.Reconcile(Array.Empty<IDeviceInfo>());                                     // miss 2 -> lost

        Assert.Single(lost);
        Assert.Empty(finder.Devices);
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
        var inner = new StubDeviceFinder();
        var finder = NewFinder(inner);

        finder.Dispose();
        Assert.Equal(1, inner.DisposeCount);

        // The second dispose returns early: the inner finder is not disposed a second time, and
        // the finder stays disposed, so Start still throws (DeviceFinderBaseTests' post-dispose
        // contract).
        finder.Dispose();
        Assert.Equal(1, inner.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => finder.Start());
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
    public async Task Start_WhenEveryLaterPassTimesOut_KeepsTheLiveSetAndRaisesNothing()
    {
        // The degraded pass. The first pass finds the device; every later pass runs out of its
        // own PassTimeout and the finder answers with OperationCanceledException. The loop treats
        // that as a skipped pass: the device is NOT missed (MissThreshold is 1 here, so a single
        // counted miss would lose it), and it is NOT an error either.
        //
        // That is the behaviour issue #725 asks to keep and to make visible: with no event on any
        // channel, a consumer watching this finder sees exactly what it sees during a healthy
        // quiet pass, even though discovery has stopped working.
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var inner = new TimingOutDeviceFinder(new[] { (IDeviceInfo)device });
        using var finder = new ContinuousDeviceFinder(inner, new ContinuousDiscoveryOptions
        {
            MissThreshold = 1,
            Interval = TimeSpan.FromMilliseconds(10),
            PassTimeout = TimeSpan.FromMilliseconds(50)
        });

        var discovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => inner.PassesStarted >= 4,
            () => $"the finder only started {inner.PassesStarted} passes; three timed-out passes were needed",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Single(discovered);
        Assert.Empty(lost);
        Assert.Empty(errors);
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, device));
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
        await WaitUntil.ThatAsync(
            () => inner.PassesStarted > 0,
            "the finder never started a pass",
            WaitTimeout);

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
        await WaitUntil.ThatAsync(
            () => inner.PassesStarted > 0,
            "the finder never started a pass",
            WaitTimeout);

        var sw = Stopwatch.StartNew();
        finder.Dispose(); // must cancel the in-flight pass, not wait out the 30s PassTimeout
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.Elapsed}; expected prompt cancellation.");
        Assert.True(inner.Disposed); // inner finder disposed only after the loop stopped
    }

    #endregion

    #region Pass reports

    // The cadence every report test runs at. Small enough to keep the tests quick, and the
    // interval is what a report carries as NextPassDelay, so it appears in the expected values.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ReportPassTimeout = TimeSpan.FromMilliseconds(50);

    private static ContinuousDeviceFinder NewReportingFinder(
        IDeviceFinder inner,
        IProgress<DiscoveryPassReport>? progress)
        => new(inner, new ContinuousDiscoveryOptions
        {
            // One miss loses a device, so a pass that finds nothing shows up as a loss in the
            // very next report instead of the one after it.
            MissThreshold = 1,
            Interval = ReportInterval,
            PassTimeout = ReportPassTimeout,
            Progress = progress
        });

    private static DiscoveryPassReport Report(
        DiscoveryPassOutcome outcome,
        int devicesSeen = 0,
        int newDevices = 0,
        int metadataUpdates = 0,
        int lostDevices = 0,
        int liveDeviceCount = 0)
        => new(outcome, devicesSeen, newDevices, metadataUpdates, lostDevices, liveDeviceCount, ReportInterval);

    [Fact]
    public async Task Start_WithEveryKindOfPassInARow_ReportsThemInOrder()
    {
        // The whole point of issue #725 in one assertion. Six passes, each a different kind, and
        // the sink sees six reports that tell them apart. Without the sink, passes 1, 3 and 4
        // raise nothing at all, so a consumer sees the same thing for a pass that broke, a pass
        // that was quiet, and a pass that changed a device's address.
        var device = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.10"));
        var readdressed = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.20"));
        var progress = new RecordingPassProgress();
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.TimesOut(),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(readdressed),
            ScriptedPassFinder.Finds(),
            ScriptedPassFinder.Throws(new InvalidOperationException("the transport went away")));

        using var finder = NewReportingFinder(inner, progress);
        finder.ScanError += (_, _) => { };

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 6,
            () => $"only {progress.Count} of the 6 scripted passes were reported",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Equal(
            [
                // 1. The pass ran out of its own PassTimeout. Nothing was reconciled, so every
                //    count is zero — and the live set is empty, which it says rather than guesses.
                Report(DiscoveryPassOutcome.TimedOut),

                // 2. A device arrived. DeviceDiscovered was raised for it before this report.
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, newDevices: 1, liveDeviceCount: 1),

                // 3. The quiet pass: the same device, unchanged. No event, and now a report that
                //    says the pass worked and found nothing new.
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, liveDeviceCount: 1),

                // 4. The same device on a new IP address. Still no event, because it neither
                //    arrived nor went away, so this count is the only way to learn the row is
                //    out of date.
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, metadataUpdates: 1, liveDeviceCount: 1),

                // 5. The device is gone. MissThreshold is 1, so it leaves the live set at once
                //    and DeviceLost was raised before this report.
                Report(DiscoveryPassOutcome.Completed, lostDevices: 1),

                // 6. The pass threw. The exception itself went to ScanError; the report says the
                //    pass produced nothing usable.
                Report(DiscoveryPassOutcome.Failed),
            ],
            progress.Snapshot().Take(6));
    }

    [Fact]
    public async Task Start_WhenEveryLaterPassTimesOut_KeepsReportingTimedOutWithTheDeviceStillLive()
    {
        // The degraded run, which is the case a user actually suffers: discovery stopped working
        // but the device list still looks right. Every report says TimedOut and says one device
        // is still live, so a consumer can mark the list as stale instead of trusting it.
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var progress = new RecordingPassProgress();
        var inner = new TimingOutDeviceFinder(new[] { (IDeviceInfo)device });
        using var finder = NewReportingFinder(inner, progress);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 4,
            () => $"only {progress.Count} passes were reported",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Equal(
            [
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, newDevices: 1, liveDeviceCount: 1),
                Report(DiscoveryPassOutcome.TimedOut, liveDeviceCount: 1),
                Report(DiscoveryPassOutcome.TimedOut, liveDeviceCount: 1),
                Report(DiscoveryPassOutcome.TimedOut, liveDeviceCount: 1),
            ],
            progress.Snapshot().Take(4));
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, device));
    }

    [Fact]
    public async Task Start_WhenATimedOutPassAnswersWithPartOfTheSystem_KeepsTheRest()
    {
        // The same degraded run, but with the finder that answers a timeout by handing back what
        // it had collected instead of throwing. SerialDeviceFinder is that finder: it catches the
        // pass cancellation and returns the port probes that already finished, and
        // AllTransportsDeviceFinder passes that through. The pass therefore arrives as a SHORT
        // list of real devices, and the device the probe never reached must keep its place.
        //
        // MissThreshold is 1 here, so counting that device as a miss would drop a device that is
        // still plugged in on the first slow pass.
        var reached = Serial("SN-1", port: "COM3");
        var notReached = Serial("SN-2", port: "COM7");
        var progress = new RecordingPassProgress();
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.Finds(reached, notReached),
            ScriptedPassFinder.TimesOutReturning(reached));

        using var finder = NewReportingFinder(inner, progress);
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 3,
            () => $"only {progress.Count} passes were reported",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Empty(lost);
        Assert.Empty(errors);
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, notReached));
        Assert.Equal(2, finder.Devices.Count);

        Assert.Equal(
            [
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 2, newDevices: 2, liveDeviceCount: 2),

                // The pass saw one of the two devices and ran out of time. It is reported as
                // TimedOut with one device seen and nothing lost, and both devices stay live.
                Report(DiscoveryPassOutcome.TimedOut, devicesSeen: 1, liveDeviceCount: 2),
                Report(DiscoveryPassOutcome.TimedOut, devicesSeen: 1, liveDeviceCount: 2),
            ],
            progress.Snapshot().Take(3));
    }

    [Fact]
    public async Task Start_WhenOnlyMetadataChanged_ReportsTheUpdateAndStillRaisesNoEvent()
    {
        // The report is additive, not a replacement: the metadata refresh is still silent on the
        // event channels, exactly as the guarding test above pins it.
        var device = Serial("SN-1", port: "COM3");
        var moved = Serial("SN-1", port: "COM7");
        var progress = new RecordingPassProgress();
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(moved),
            ScriptedPassFinder.Finds(moved));

        using var finder = NewReportingFinder(inner, progress);
        var discovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 3,
            () => $"only {progress.Count} passes were reported",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Equal(
            [
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, newDevices: 1, liveDeviceCount: 1),
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, metadataUpdates: 1, liveDeviceCount: 1),
                Report(DiscoveryPassOutcome.Completed, devicesSeen: 1, liveDeviceCount: 1),
            ],
            progress.Snapshot().Take(3));
        Assert.Single(discovered);
        Assert.Empty(lost);
        Assert.Empty(errors);
        Assert.Equal("COM7", finder.Devices[0].PortName);
    }

    [Fact]
    public async Task Start_ReportsThePassAfterItsOwnEvents()
    {
        // Order within a pass: the events first, then the summary that explains them. A consumer
        // that redraws on the report therefore sees a live set the report already matches.
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var order = new List<string>();
        var progress = new RecordingPassProgress(_ =>
        {
            lock (order)
            {
                order.Add("report");
            }
        });
        var inner = new ScriptedPassFinder(ScriptedPassFinder.Finds(device));

        using var finder = NewReportingFinder(inner, progress);
        finder.DeviceDiscovered += (_, _) =>
        {
            lock (order)
            {
                order.Add("discovered");
            }
        };

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 1,
            "the first pass was never reported",
            WaitTimeout);
        await finder.StopAsync();

        lock (order)
        {
            Assert.Equal(["discovered", "report"], order.Take(2));
        }
    }

    [Fact]
    public async Task Start_WithNoProgressSink_BehavesExactlyAsBefore()
    {
        // The seam is optional and additive. A caller that sets nothing gets the default, and the
        // same script produces the same events, the same live set and the same errors as it does
        // with a sink attached.
        Assert.Null(new ContinuousDiscoveryOptions().Progress);

        var device = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.10"));
        var readdressed = Wifi("AA:BB:CC:DD:EE:01", ip: IPAddress.Parse("192.168.1.20"));
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.TimesOut(),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(readdressed),
            ScriptedPassFinder.Finds(),
            ScriptedPassFinder.Throws(new InvalidOperationException("the transport went away")));

        using var finder = NewReportingFinder(inner, progress: null);
        var discovered = new List<IDeviceInfo>();
        var lost = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.DeviceLost += (_, e) => lost.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => inner.PassesStarted >= 6 && errors.Count >= 1,
            () => $"the script was not played out: {inner.PassesStarted} passes, {errors.Count} errors",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Same(device, Assert.Single(discovered));
        Assert.Same(readdressed, Assert.Single(lost));
        Assert.IsType<InvalidOperationException>(errors[0]);
        Assert.Empty(finder.Devices);
        Assert.True(finder.IsRunning == false);
    }

    [Fact]
    public async Task Start_WhenTheProgressHandlerThrows_KeepsTheLoopAndTheDevices()
    {
        // A consumer's status handler must not be able to stop discovery or discard the devices a
        // pass found. The events for them are raised before the report, and the exception goes to
        // ScanError, which is where this class sends every other faulty handler.
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var progress = new RecordingPassProgress(_ => throw new InvalidOperationException("handler blew up"));
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device));

        using var finder = NewReportingFinder(inner, progress);
        var discovered = new List<IDeviceInfo>();
        var errors = new List<Exception>();
        finder.DeviceDiscovered += (_, e) => discovered.Add(e.DeviceInfo);
        finder.ScanError += (_, e) => errors.Add(e.Exception);

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 3,
            () => $"the loop stopped after {progress.Count} reports",
            WaitTimeout);

        Assert.True(finder.IsRunning);
        Assert.Same(device, Assert.Single(discovered));
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, device));
        Assert.All(errors, error => Assert.IsType<InvalidOperationException>(error));
        Assert.NotEmpty(errors);

        await finder.StopAsync();
    }

    [Fact]
    public async Task Start_WhenTheProgressHandlerThrowsOperationCanceled_DoesNotStopTheLoop()
    {
        // Cancellation belongs to Stop and Dispose. A handler that throws this exception — by
        // checking a token of its own, say — must not look like one.
        var device = Wifi("AA:BB:CC:DD:EE:01");
        var progress = new RecordingPassProgress(_ => throw new OperationCanceledException("not the loop's token"));
        var inner = new ScriptedPassFinder(
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device),
            ScriptedPassFinder.Finds(device));

        using var finder = NewReportingFinder(inner, progress);
        finder.ScanError += (_, _) => { };

        finder.Start();
        await WaitUntil.ThatAsync(
            () => progress.Count >= 3,
            () => $"the loop stopped after {progress.Count} reports",
            WaitTimeout);

        Assert.True(finder.IsRunning);
        Assert.Contains(finder.Devices, d => ReferenceEquals(d, device));

        await finder.StopAsync();
    }

    [Fact]
    public async Task Start_WhenTheSinkIsSetAfterConstruction_ReportsNothing()
    {
        // Every value on the options object is read once, at construction. The sink is no
        // different, and the doc comment says so.
        var options = new ContinuousDiscoveryOptions
        {
            MissThreshold = 1,
            Interval = ReportInterval,
            PassTimeout = ReportPassTimeout
        };
        var inner = new ScriptedPassFinder(ScriptedPassFinder.Finds(Wifi("AA:BB:CC:DD:EE:01")));
        using var finder = new ContinuousDeviceFinder(inner, options);

        var progress = new RecordingPassProgress();
        options.Progress = progress;

        finder.Start();
        await WaitUntil.ThatAsync(
            () => inner.PassesStarted >= 3,
            () => $"the finder only started {inner.PassesStarted} passes",
            WaitTimeout);
        await finder.StopAsync();

        Assert.Equal(0, progress.Count);
    }

    #endregion

    #region Scan loop helpers

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
        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

#pragma warning disable CS0067 // Events are part of the interface but unused by this stub.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
            => Task.FromResult(Enumerable.Empty<IDeviceInfo>());

        public void Dispose() => DisposeCount++;
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
    /// Records every pass report in arrival order, and can throw from the handler afterwards to
    /// stand in for a consumer whose status code is broken.
    /// </summary>
    /// <remarks>
    /// A direct <see cref="IProgress{T}"/> rather than <see cref="Progress{T}"/>: the BCL class
    /// posts to a synchronization context, which would both reorder the assertions and hide a
    /// throwing handler behind its own marshalling. This one reports on the scan loop's own
    /// thread, which is the strict case.
    /// </remarks>
    private sealed class RecordingPassProgress(Action<DiscoveryPassReport>? onReport = null)
        : IProgress<DiscoveryPassReport>
    {
        private readonly List<DiscoveryPassReport> _reports = [];

        public int Count
        {
            get
            {
                lock (_reports)
                {
                    return _reports.Count;
                }
            }
        }

        public IReadOnlyList<DiscoveryPassReport> Snapshot()
        {
            lock (_reports)
            {
                return _reports.ToList();
            }
        }

        public void Report(DiscoveryPassReport value)
        {
            // Recorded before the handler runs, so a throwing handler still shows what it saw.
            lock (_reports)
            {
                _reports.Add(value);
            }

            onReport?.Invoke(value);
        }
    }

    /// <summary>
    /// Finder that plays one scripted behaviour per pass — found these devices, ran out of time,
    /// threw. Pass kinds are written as <see cref="Finds"/>, <see cref="TimesOut"/> and
    /// <see cref="Throws"/>, so a test reads as the sequence of passes it means to produce.
    /// </summary>
    /// <remarks>
    /// Once the script is spent, the last step repeats for as long as the loop runs. Finding
    /// nothing instead would quietly add a pass the test did not ask for — one that loses the
    /// device and raises DeviceLost — in the gap between the test's last assertion and the stop
    /// taking effect, which makes an otherwise deterministic assertion depend on the clock.
    /// </remarks>
    private sealed class ScriptedPassFinder : IDeviceFinder
    {
        private readonly Queue<Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>>> _steps;
        private Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>>? _lastStep;
        private int _passesStarted;

        public ScriptedPassFinder(params Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>>[] steps)
        {
            _steps = new Queue<Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>>>(steps);
        }

        public int PassesStarted => Volatile.Read(ref _passesStarted);

#pragma warning disable CS0067 // Events are part of the interface but unused by this fake.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        /// <summary>A pass that answers with exactly these devices.</summary>
        public static Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>> Finds(params IDeviceInfo[] devices)
            => _ => Task.FromResult<IEnumerable<IDeviceInfo>>(devices);

        /// <summary>
        /// A pass that never answers on its own and throws when the pass timeout cancels it,
        /// which is what a finder that honours cancellation does.
        /// </summary>
        public static Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>> TimesOut()
            => async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Array.Empty<IDeviceInfo>();
            };

        /// <summary>
        /// A pass that never answers on its own and, when the pass timeout cancels it, returns
        /// the part it had collected instead of throwing. That is what
        /// <c>SerialDeviceFinder.DiscoverAsync(CancellationToken)</c> does: it catches the
        /// cancellation and returns the port probes that already finished, so a timed-out pass
        /// arrives as a short list of real devices rather than as an exception.
        /// </summary>
        public static Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>> TimesOutReturning(
            params IDeviceInfo[] collectedSoFar)
            => async token =>
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetResult(true)))
                {
                    await cancelled.Task.ConfigureAwait(false);
                }

                return collectedSoFar;
            };

        /// <summary>A pass that fails.</summary>
        public static Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>> Throws(Exception error)
            => _ => throw error;

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _passesStarted);

            Func<CancellationToken, Task<IEnumerable<IDeviceInfo>>> step;
            lock (_steps)
            {
                if (_steps.Count > 0)
                {
                    _lastStep = _steps.Dequeue();
                }

                step = _lastStep ?? Finds();
            }

            return step(cancellationToken);
        }

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
            => DiscoverAsync(CancellationToken.None);
    }

    /// <summary>
    /// Finder that answers one scripted pass and then never answers again: every later pass waits
    /// on the pass token and throws <see cref="OperationCanceledException"/> when it fires. That
    /// is what a finder which honours cancellation does when
    /// <see cref="ContinuousDiscoveryOptions.PassTimeout"/> expires, so it is how a timed-out pass
    /// is reproduced without a wall-clock sleep.
    /// </summary>
    private sealed class TimingOutDeviceFinder : IDeviceFinder
    {
        private readonly IReadOnlyList<IDeviceInfo> _firstPassResults;
        private int _passesStarted;

        public TimingOutDeviceFinder(IReadOnlyList<IDeviceInfo> firstPassResults)
        {
            _firstPassResults = firstPassResults;
        }

        public int PassesStarted => Volatile.Read(ref _passesStarted);

#pragma warning disable CS0067 // Events are part of the interface but unused by this fake.
        public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;
        public event EventHandler? DiscoveryCompleted;
#pragma warning restore CS0067

        public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _passesStarted) == 1)
            {
                return _firstPassResults;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Array.Empty<IDeviceInfo>();
        }

        public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
            => DiscoverAsync(CancellationToken.None);
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
