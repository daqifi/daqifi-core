using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Issue #487: both Windows discovery providers ran their own <c>Win32_PnPEntity</c> query per COM
/// port, per discovery pass. These cover the shared map that answers all of those questions with
/// one query, and the caption parsing that replaces the per-port <c>LIKE '%(COMn)%'</c> predicate.
/// The map takes its rows from an injected query, so the parsing and caching are exercised on any
/// platform without WMI.
/// </summary>
public class WindowsPnpPortMapTests
{
    private static PnpPortEntity Usb(string port, string vidPid = "VID_04D8&PID_F794") =>
        new($@"USB\{vidPid}\SERIAL{port}", $"USB Serial Device ({port})");

    // ---- caption parsing -------------------------------------------------

    [Fact]
    public void BuildMap_NoEntities_IsEmpty()
    {
        Assert.Empty(WindowsPnpPortMap.BuildMap([]));
    }

    [Fact]
    public void BuildMap_TypicalUsbSerialCaption_MapsThePortToItsDeviceId()
    {
        var map = WindowsPnpPortMap.BuildMap([Usb("COM9")]);

        Assert.Equal([@"USB\VID_04D8&PID_F794\SERIALCOM9"], map["COM9"]);
    }

    [Fact]
    public void BuildMap_ManyPorts_MapsEachOfThem()
    {
        var map = WindowsPnpPortMap.BuildMap(
        [
            Usb("COM3"),
            new PnpPortEntity(@"ACPI\PNP0501\1", "Communications Port (COM1)"),
            new PnpPortEntity(@"BTHENUM\{0000}\7&2", "Standard Serial over Bluetooth link (COM7)")
        ]);

        Assert.Equal(3, map.Count);
        Assert.Equal([@"ACPI\PNP0501\1"], map["COM1"]);
        Assert.Equal([@"BTHENUM\{0000}\7&2"], map["COM7"]);
    }

    [Fact]
    public void BuildMap_CaptionNamingNoPort_IsSkipped()
    {
        // PNPClass='Ports' also covers printer ports and the like, whose captions carry no
        // "(COMn)" token at all.
        var map = WindowsPnpPortMap.BuildMap([new PnpPortEntity(@"ACPI\PNP0400\4", "Printer Port (LPT1)")]);

        Assert.Empty(map);
    }

    [Fact]
    public void BuildMap_EntityMissingItsDeviceIdOrCaption_IsSkipped()
    {
        var map = WindowsPnpPortMap.BuildMap(
        [
            new PnpPortEntity(null, "USB Serial Device (COM4)"),
            new PnpPortEntity(@"USB\VID_04D8&PID_F794\X", null),
            new PnpPortEntity("", "USB Serial Device (COM5)"),
            new PnpPortEntity(@"USB\VID_04D8&PID_F794\Y", "")
        ]);

        Assert.Empty(map);
    }

    /// <summary>
    /// The predicate this replaces matched a caption <em>substring</em>, so more than one entity
    /// could answer for one port. Both are kept, in enumeration order, because the two callers
    /// pick different entries out of that set.
    /// </summary>
    [Fact]
    public void BuildMap_TwoEntitiesClaimingOnePort_KeepsBothInEnumerationOrder()
    {
        var map = WindowsPnpPortMap.BuildMap(
        [
            new PnpPortEntity(@"ACPI\PNP0501\1", "Legacy bridge (COM9)"),
            Usb("COM9")
        ]);

        Assert.Equal([@"ACPI\PNP0501\1", @"USB\VID_04D8&PID_F794\SERIALCOM9"], map["COM9"]);
    }

    [Fact]
    public void BuildMap_OneCaptionNamingThePortTwice_ListsThatEntityOnce()
    {
        var map = WindowsPnpPortMap.BuildMap(
            [new PnpPortEntity(@"USB\VID_04D8&PID_F794\Z", "Dual bridge (COM9) mirror of (COM9)")]);

        Assert.Equal([@"USB\VID_04D8&PID_F794\Z"], map["COM9"]);
    }

    [Fact]
    public void BuildMap_OneCaptionNamingTwoPorts_MapsBoth()
    {
        var map = WindowsPnpPortMap.BuildMap(
            [new PnpPortEntity(@"USB\VID_04D8&PID_F794\Z", "Dual bridge (COM9) and (COM10)")]);

        Assert.Equal([@"USB\VID_04D8&PID_F794\Z"], map["COM9"]);
        Assert.Equal([@"USB\VID_04D8&PID_F794\Z"], map["COM10"]);
    }

    /// <summary>
    /// The parentheses are load-bearing: <c>LIKE '%(COM9)%'</c> never matched the entity captioned
    /// "(COM90)", and neither may the token parsing that replaces it.
    /// </summary>
    [Fact]
    public void BuildMap_APortWhoseNumberIsAPrefixOfAnother_IsNotConfusedWithIt()
    {
        var map = WindowsPnpPortMap.BuildMap([Usb("COM90")]);

        Assert.False(map.ContainsKey("COM9"));
        Assert.Single(map["COM90"]);
    }

    [Fact]
    public void GetDeviceIds_MatchesThePortNameCaseInsensitively()
    {
        var map = new WindowsPnpPortMap(() => [Usb("COM9")]);

        Assert.Single(map.GetDeviceIds("com9"));
    }

    [Fact]
    public void GetDeviceIds_ForAPortNoEntityClaims_IsEmpty()
    {
        var map = new WindowsPnpPortMap(() => [Usb("COM9")]);

        Assert.Empty(map.GetDeviceIds("COM4"));
    }

    [Fact]
    public void GetDeviceIds_ForAnEmptyPortName_IsEmptyWithoutQuerying()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return [Usb("COM9")];
        });

        Assert.Empty(map.GetDeviceIds(""));
        Assert.Empty(map.GetDeviceIds("   "));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    /// <summary>
    /// A lookup hands out part of a map several threads are reading, so it must hand out something
    /// none of them can change: a caller that cast it back to a mutable list would be editing every
    /// other caller's view of that port.
    /// </summary>
    [Fact]
    public void WhatALookupReturns_CannotBeMutatedByItsCaller()
    {
        var map = new WindowsPnpPortMap(() => [Usb("COM3")]);

        var deviceIds = map.GetDeviceIds("COM3");

        Assert.Throws<NotSupportedException>(() => ((IList<string>)deviceIds).Add("injected"));
        Assert.Single(map.GetDeviceIds("COM3"));
    }

    // ---- caching ---------------------------------------------------------

    /// <summary>
    /// The point of the change, and the issue's first success criterion: a pass classifying every
    /// COM port on the machine costs one query, not one per port.
    /// </summary>
    [Fact]
    public void ClassifyingEveryPortInAPass_QueriesOnce()
    {
        var ports = Enumerable.Range(1, 12).Select(n => $"COM{n}").ToArray();
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return ports.Select(p => Usb(p)).ToArray();
        });

        foreach (var port in ports)
        {
            Assert.Single(map.GetDeviceIds(port));
        }

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1L, map.QueryCount);
    }

    /// <summary>
    /// The issue's second success criterion: two passes inside the window issue no further queries.
    /// </summary>
    [Fact]
    public void ASecondPassInsideTheWindow_QueriesNoFurther()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return [Usb("COM3"), Usb("COM7")];
        });

        for (var pass = 0; pass < 2; pass++)
        {
            map.GetDeviceIds("COM3");
            map.GetDeviceIds("COM7");
        }

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void ALookupAfterTheWindow_QueriesAgain()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(
            () =>
            {
                Interlocked.Increment(ref calls);
                return [Usb("COM3")];
            },
            cacheDurationMs: 1);

        Assert.Single(map.GetDeviceIds("COM3"));
        Thread.Sleep(20);
        Assert.Single(map.GetDeviceIds("COM3"));

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void WithCachingDisabled_EveryLookupQueries()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(
            () =>
            {
                Interlocked.Increment(ref calls);
                return [Usb("COM3")];
            },
            cacheDurationMs: 0);

        map.GetDeviceIds("COM3");
        map.GetDeviceIds("COM3");
        map.GetDeviceIds("COM3");

        Assert.Equal(3, Volatile.Read(ref calls));
    }

    /// <summary>
    /// Ports are classified from several threads at once, which is the case the shared map exists
    /// for: they cost one query between them, not one each.
    /// </summary>
    [Fact]
    public void ConcurrentLookups_QueryOnce()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);

            // Wide enough that every other caller is definitely inside GetDeviceIds before this
            // returns.
            Thread.Sleep(30);
            return [Usb("COM3")];
        });

        const int callers = 8;
        using var everyoneReady = new Barrier(callers);
        var found = new int[callers];

        // Dedicated threads, not the thread pool: the barrier requires all of them to be running at
        // once, which a pool that is ramping up cannot promise.
        var threads = new Thread[callers];
        for (var i = 0; i < callers; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                everyoneReady.SignalAndWait();
                found[index] = map.GetDeviceIds("COM3").Count;
            })
            {
                IsBackground = true
            };
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "A concurrent lookup never returned.");
        }

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(found, count => Assert.Equal(1, count));
    }

    // ---- the two staleness rules ----------------------------------------

    /// <summary>
    /// The descriptor provider's rule: a miss is left alone. Re-querying for it would put a WMI
    /// query back on the per-port path on any machine with a COM port that has no
    /// <c>PNPClass='Ports'</c> entity, which is the cost this class removes.
    /// </summary>
    [Fact]
    public void AMissInsideTheWindow_DoesNotQueryAgain()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return [Usb("COM3")];
        });

        map.GetDeviceIds("COM3");
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(map.GetDeviceIds("COM8"));
        }

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>
    /// The location provider's rule: for it a miss is a real answer — no location key at all for a
    /// port that has just probed as a DAQiFi device — so a port that appeared since the map was
    /// built must still resolve.
    /// </summary>
    [Fact]
    public void AMissWithRefreshOnMiss_RebuildsAndSeesTheNewPort()
    {
        var calls = 0;
        PnpPortEntity[] present = [Usb("COM3")];
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return present;
        });

        Assert.Single(map.GetDeviceIds("COM3"));
        Assert.Empty(map.GetDeviceIds("COM8", refreshOnMiss: true));

        // COM8 is plugged in well inside the cache window.
        present = [Usb("COM3"), Usb("COM8")];

        Assert.Single(map.GetDeviceIds("COM8", refreshOnMiss: true));
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void AHitWithRefreshOnMiss_DoesNotQueryAgain()
    {
        var calls = 0;
        var map = new WindowsPnpPortMap(() =>
        {
            Interlocked.Increment(ref calls);
            return [Usb("COM3")];
        });

        map.GetDeviceIds("COM3", refreshOnMiss: true);
        map.GetDeviceIds("COM3", refreshOnMiss: true);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // ---- failure ---------------------------------------------------------

    /// <summary>
    /// A failed rebuild must not install an empty map: that would read as "every port just
    /// disappeared" for the rest of the window, turning one WMI hiccup into a pass that classifies
    /// nothing. The failure is reported to the caller, which already treats it as "unknown".
    /// </summary>
    [Fact]
    public void AFailedRebuild_LeavesTheLastGoodMapInPlace()
    {
        var fail = false;
        var map = new WindowsPnpPortMap(() =>
            fail ? throw new InvalidOperationException("WMI unavailable") : [Usb("COM3")]);

        Assert.Single(map.GetDeviceIds("COM3"));

        fail = true;
        Assert.Throws<InvalidOperationException>(() => map.GetDeviceIds("COM8", refreshOnMiss: true));

        // Still answering from the map built before the failure.
        Assert.Single(map.GetDeviceIds("COM3"));
    }
}
